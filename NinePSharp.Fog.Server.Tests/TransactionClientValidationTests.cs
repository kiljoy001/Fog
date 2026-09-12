using System.Text;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class TransactionClientValidationTests
{
    [Fact]
    public void ConstructorRejectsEachInvalidLimit()
    {
        Task<Stream> Connect(CancellationToken _) => throw new InvalidOperationException();
        Assert.Throws<ArgumentNullException>(() => new FogTransactionClient(null!, "worker", 256, 1, TimeSpan.FromSeconds(1)));
        foreach (var args in new[] { ("worker", 255U, 1, 1), ("worker", 2147483648U, 1, 1), ("worker", 256U, 0, 1), ("worker", 256U, 1, 0), ("", 256U, 1, 1) })
            Assert.Equal("Invalid transaction client limits.", Assert.Throws<ArgumentException>(() =>
                new FogTransactionClient(Connect, args.Item1, args.Item2, args.Item3, TimeSpan.FromSeconds(args.Item4))).Message);
        _ = new FogTransactionClient(Connect, "worker", int.MaxValue, 1, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ValidationChecksCombinedInputSizeAndEveryPathBeforeConnecting()
    {
        int connections = 0;
        var client = new FogTransactionClient(_ => { connections++; throw new IOException(); }, "worker", 256, 2, TimeSpan.FromSeconds(1));
        async Task Invalid(string service, Dictionary<string, byte[]> inputs, string[] outputs) =>
            Assert.Equal("Invalid bounded transaction request.", (await Assert.ThrowsAsync<ArgumentException>(() => client.ExecuteAsync(service, inputs, outputs))).Message);
        await Invalid("fixture", new() { ["payload"] = [] }, ["reply"]);
        await Invalid("fixture", new() { ["request"] = [1, 2], ["payload"] = [3] }, ["reply"]);
        await Invalid("fixture", new() { ["request"] = [] }, []);
        await Invalid("fixture", new() { ["request"] = [] }, ["reply", "reply"]);
        foreach (string name in new[] { "", ".", "a/b", "aé", new string('x', 65) })
        {
            await Invalid(name, new() { ["request"] = [] }, ["reply"]);
            await Invalid("fixture", new() { ["request"] = [], [name] = [] }, ["reply"]);
            await Invalid("fixture", new() { ["request"] = [] }, ["reply", name]);
        }
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task ExactInputAndOutputBoundsAndMaximumLengthNamesAreAccepted()
    {
        using var peer = new ControlPeer { Id = new string('a', 64) + "-18446744073709551615" };
        string name = new string('A', 61) + "0_-";
        peer.Outputs[name] = [1, 2];
        var client = Client(peer, maximum: 2);
        var result = await client.ExecuteAsync(name, new Dictionary<string, byte[]> { ["request"] = [3], [name] = [4] }, [name]);
        Assert.Equal(new byte[] { 1, 2 }, result[name]);
        Assert.Equal(new byte[] { 3 }, peer.Inputs["request"].ToArray());
        Assert.Equal(new byte[] { 4 }, peer.Inputs[name].ToArray());
        Assert.Equal(1, peer.Commits);
        Assert.True(peer.Released);
        Assert.Single(peer.Fids); // Only the attached root survives until disconnect.
    }

    public static IEnumerable<object[]> InvalidIds()
    {
        string boot = new string('a', 64);
        foreach (string value in new[] { "", boot, boot + "-1", boot + "_1\n", "g" + boot[1..] + "-1\n", boot + "-0\n", boot + "-01\n", boot + "-+1\n", boot + "--1\n", boot + "-x\n", boot + "-18446744073709551616\n", boot + "-184467440737095516150\n" })
            yield return [value];
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task MalformedCloneIdentitiesNeverReachUploadOrCommit(string id)
    {
        using var peer = new ControlPeer { CloneBytes = Encoding.ASCII.GetBytes(id) };
        var error = await Assert.ThrowsAsync<IOException>(() => Client(peer).ExecuteAsync("fixture", Request(), ["reply"]));
        Assert.Contains(error.Message, new[] { "Invalid transaction identity.", "Oversized control file." });
        Assert.Equal(0, peer.Commits);
        Assert.Empty(peer.Inputs);
    }

    [Theory]
    [InlineData("id", "Mismatched transaction identity.")]
    [InlineData("state", "Invalid transaction state.")]
    [InlineData("write", "Ambiguous partial control write.")]
    [InlineData("output", "Oversized control file.")]
    [InlineData("aggregate", "Oversized control file.")]
    [InlineData("count", "Oversized control file.")]
    public async Task MalformedPeerResultsAreRejected(string kind, string diagnostic)
    {
        using var peer = new ControlPeer();
        if (kind == "id") peer.StatusId = "wrong";
        if (kind == "state") peer.State = "unknown";
        if (kind == "write") peer.Override = request => request is Twrite w ? new Rwrite(w.Tag, (uint)w.Data.Length - 1) : null;
        if (kind == "output") peer.Outputs["reply"] = [1, 2, 3];
        if (kind == "aggregate") { peer.Outputs["reply"] = [1, 2]; peer.Outputs["extra"] = [3]; }
        if (kind == "count") peer.Override = request => request is Tread r && peer.Fids[r.Fid] == "reply" && r.Count < 10 ? new Rread(r.Tag, new byte[r.Count + 1]) : null;
        var error = await Assert.ThrowsAsync<IOException>(() => Client(peer, maximum: 2).ExecuteAsync("fixture", Request(), kind == "aggregate" ? ["reply", "extra"] : ["reply"]));
        Assert.Equal(diagnostic, error.Message);
        Assert.False(peer.Released);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("committing")]
    public async Task RetainedStatesAreReplayedWithoutReplacingInput(string state)
    {
        using var peer = new ControlPeer { State = state };
        var result = await Client(peer).ExecuteAsync("fixture", Request(), ["reply"]);
        Assert.Equal(new byte[] { 9 }, result["reply"]);
        Assert.Empty(peer.Inputs);
        Assert.Equal(1, peer.Commits);
    }

    [Fact]
    public async Task PartialWalkIsAbsenceAndDoesNotRetryOrUpload()
    {
        using var peer = new ControlPeer { Override = request => request is Twalk w ? new Rwalk(w.Tag, []) : null };
        Assert.Equal("tx-expired", (await Assert.ThrowsAsync<NinePException>(() => Client(peer).ExecuteAsync("fixture", Request(), ["reply"]))).Message);
        Assert.Empty(peer.Inputs);
        Assert.Equal(0, peer.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseFailureDoesNotDiscardCollectedResults(bool cancel)
    {
        using var peer = new ControlPeer();
        peer.Override = request => request is Twrite w && w.Data.Span.SequenceEqual("release\n"u8) ?
            cancel ? throw new OperationCanceledException() : new Rerror(w.Tag, "denied") : null;
        var result = await Client(peer).ExecuteAsync("fixture", Request(), ["reply"]);
        Assert.Equal(new byte[] { 9 }, result["reply"]);
        Assert.Equal(1, peer.Commits);
    }

    [Fact]
    public async Task DeadlineCancelsAHungConnectionAndInputsAreFrozenBeforeConnecting()
    {
        var client = new FogTransactionClient(async cancellation =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException();
        }, "worker", 256, 1024, TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync("fixture", Request(), ["reply"]).WaitAsync(TimeSpan.FromSeconds(5)));
        using var peer = new ControlPeer();
        var input = Request();
        var frozen = new FogTransactionClient(_ => { input["request"][0] = 88; return Task.FromResult<Stream>(peer); }, "worker", 256, 1024, TimeSpan.FromSeconds(5));
        await frozen.ExecuteAsync("fixture", input, ["reply"]);
        Assert.Equal(new byte[] { 1 }, peer.Inputs["request"].ToArray());
    }

    private static Dictionary<string, byte[]> Request() => new() { ["request"] = [1] };

    [Theory]
    [InlineData("connect")]
    [InlineData("version")]
    [InlineData("attach")]
    [InlineData("walk-clone")]
    [InlineData("open-clone")]
    [InlineData("read-clone")]
    [InlineData("clunk-clone")]
    [InlineData("read-status")]
    [InlineData("walk-request")]
    [InlineData("open-request")]
    [InlineData("write-request")]
    [InlineData("clunk-request")]
    [InlineData("commit")]
    [InlineData("read-reply")]
    [InlineData("release")]
    public async Task AsyncTransactionStagesDoNotRequireTheCallingSynchronizationContext(string stage)
    {
        using var peer = new ControlPeer();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Resume = resume;
        peer.PauseAt = stage;
        var client = new FogTransactionClient(async _ =>
        {
            if (stage == "connect") await resume.Task.ConfigureAwait(false);
            return peer;
        }, "worker", 256, 1024, TimeSpan.FromSeconds(10));
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        Task<IReadOnlyDictionary<string, byte[]>> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            operation = client.ExecuteAsync("fixture", Request(), ["reply"]);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.False(operation.IsCompleted);
        resume.SetResult();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 9 }, result["reply"]);
        Assert.Equal(0, context.Posts);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private static FogTransactionClient Client(ControlPeer peer, int maximum = 1024) =>
        new(_ => Task.FromResult<Stream>(peer), "worker", 256, maximum, TimeSpan.FromSeconds(5));

    private sealed class ControlPeer : Stream
    {
        internal string Id = new string('a', 64) + "-1";
        internal byte[]? CloneBytes;
        internal string? StatusId;
        internal string State = "staging";
        internal readonly Dictionary<string, byte[]> Outputs = new() { ["reply"] = [9] };
        internal readonly Dictionary<string, MemoryStream> Inputs = new();
        internal readonly Dictionary<uint, string> Fids = new();
        internal Func<ISerializable, ISerializable?>? Override;
        internal int Commits;
        internal bool Released;
        internal string? PauseAt;
        internal TaskCompletionSource? Resume;
        private MemoryStream response = new();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ISerializable request = (MessageTypes)buffer.Span[4] switch
            {
                MessageTypes.Tversion => new Tversion(buffer.Span),
                MessageTypes.Tattach => new Tattach(buffer.Span),
                MessageTypes.Twalk => new Twalk(buffer.Span),
                MessageTypes.Topen => new Topen(buffer.Span),
                MessageTypes.Tread => new Tread(buffer.Span),
                MessageTypes.Twrite => new Twrite(buffer),
                MessageTypes.Tclunk => new Tclunk(buffer.Span),
                _ => throw new InvalidOperationException(),
            };
            string stage = request switch
            {
                Tversion => "version",
                Tattach => "attach",
                Twalk walk => "walk-" + walk.Wname[^1],
                Topen open => "open-" + Fids[open.Fid],
                Tread read => "read-" + Fids[read.Fid],
                Tclunk clunk => "clunk-" + Fids[clunk.Fid],
                Twrite write when Fids[write.Fid] == "ctl" => Encoding.ASCII.GetString(write.Data.Span).TrimEnd('\n'),
                Twrite write => "write-" + Fids[write.Fid],
                _ => "",
            };
            if (stage == PauseAt) await Resume!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            ISerializable reply = Override?.Invoke(request) ?? Respond(request);
            response.Dispose();
            response = new MemoryStream(SequentialClientTests.Frame(reply));
        }

        private ISerializable Respond(ISerializable request)
        {
            switch (request)
            {
                case Tversion v: Fids.Clear(); Inputs.Clear(); return new Rversion(v.Tag, v.MSize, "9P2000");
                case Tattach a: Fids.Add(a.Fid, "/"); return new Rattach(a.Tag, default);
                case Twalk w: Fids.Add(w.NewFid, w.Wname[^1]); return new Rwalk(w.Tag, w.Wname.Select(_ => default(Qid)).ToArray());
                case Topen o: return new Ropen(o.Tag, default, 232);
                case Tclunk c: Assert.True(Fids.Remove(c.Fid)); return new Rclunk(c.Tag);
                case Tread r:
                    byte[] content = Fids[r.Fid] switch
                    {
                        "clone" => CloneBytes ?? Encoding.ASCII.GetBytes(Id + "\n"),
                        "status" => new FogRecordSchema("fogtx-v1", ["id", "state", "error"], ["id", "state"], ["id"]).Serialize(
                            [new Dictionary<string, string?> { ["id"] = StatusId ?? Id, ["state"] = State }], 1024, 1),
                        var outputName => Outputs[outputName],
                    };
                    return new Rread(r.Tag, content.AsMemory((int)Math.Min(r.Offset, (ulong)content.Length), (int)Math.Min(r.Count, (ulong)content.Length - Math.Min(r.Offset, (ulong)content.Length))));
                case Twrite w:
                    string file = Fids[w.Fid];
                    if (file == "ctl")
                    {
                        if (w.Data.Span.SequenceEqual("commit\n"u8)) Commits++;
                        else if (w.Data.Span.SequenceEqual("release\n"u8)) Released = true;
                        else throw new InvalidOperationException("unexpected control command");
                    }
                    else
                    {
                        if (!Inputs.TryGetValue(file, out var upload)) Inputs.Add(file, upload = new());
                        Assert.Equal((ulong)upload.Length, w.Offset);
                        upload.Write(w.Data.Span);
                    }
                    return new Rwrite(w.Tag, (uint)w.Data.Length);
                default: throw new InvalidOperationException();
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => response.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
