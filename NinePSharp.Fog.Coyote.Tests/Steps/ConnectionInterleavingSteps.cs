using System.Buffers.Binary;
using System.Text;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Specifications;
using Microsoft.Extensions.Logging;
using NinePSharp.Constants;
using NinePSharp.Core.FSharp;
using NinePSharp.Fog.Coyote.Tests.Support;
using NinePSharp.Fog.Server;
using NinePSharp.Fog.Server.Tests;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;
using Reqnroll;
using Reqnroll.UnitTestProvider;

namespace NinePSharp.Fog.Coyote.Tests.Steps;

[Binding]
public sealed class ConnectionInterleavingSteps(IUnitTestRuntimeProvider runtime, IReqnrollOutputHelper output) : IDisposable
{
    private ControlFixture? fixture;
    private TimeSpan drain;
    private bool cooperative;
    private Func<Connection, Task<Dictionary<string, Reply[]>>>? race;

    public void Dispose() => fixture?.Dispose();

    [Given(@"^a node connection with ""(.*)"" open for writing, whose drain limit is (\d+) milliseconds$")]
    public void GivenConnection(string name, int milliseconds)
    {
        if (!RewritingEngine.IsAssemblyRewritten(typeof(FogNinePDispatcher).Assembly) ||
            !RewritingEngine.IsAssemblyRewritten(typeof(NinePConnectionProcessor).Assembly))
        {
            runtime.TestIgnore("the Fog and NinePSharp.Server assemblies were not rewritten with coyote rewrite");
        }

        fixture = new ControlFixture();
        drain = TimeSpan.FromMilliseconds(milliseconds);
    }

    [Given(@"^the node's writes to ""(.*)"" finish when they are released$")]
    public void GivenReleasedWrite(string name) => cooperative = false;

    [Given(@"^the node's writes to ""(.*)"" finish when they are released or cancelled$")]
    public void GivenCooperativeWrite(string name) => cooperative = true;

    [When("the client writes with tag 100, another task releases the write, and the client stats with tag 100 the moment Rwrite arrives")]
    public void WhenTagReused() => race = async connection =>
    {
        connection.Send(new Twrite(100, 2, 0, new byte[] { 1 }));
        Task released = Task.Run(connection.Release);
        Reply write = await connection.ReceiveAsync();
        connection.Send(new Tstat(100, 1));
        Reply stat = await connection.ReceiveAsync();
        await released;
        return new() { ["write"] = [write], ["stat"] = [stat] };
    };

    [When("the client writes with tag 100 and flushes it while another task releases the write")]
    public void WhenFlushRacesRelease() => race = async connection =>
    {
        connection.Send(new Twrite(100, 2, 0, new byte[] { 1 }));
        Task released = Task.Run(connection.Release);
        connection.Send(new Tflush(101, 100));
        var replies = new List<Reply> { await connection.ReceiveAsync() };
        if (replies[0].Type != MessageTypes.Rflush)
        {
            replies.Add(await connection.ReceiveAsync());
        }

        connection.Send(new Tstat(100, 1));
        Reply stat = await connection.ReceiveAsync();
        await released;
        return new() { ["replies"] = replies.ToArray(), ["stat"] = [stat] };
    };

    [When("the client writes with tag 100 and sends Tversion while another task releases the write")]
    public void WhenVersionRacesRelease() => race = async connection =>
    {
        connection.Send(new Twrite(100, 2, 0, new byte[] { 1 }));
        Task released = Task.Run(connection.Release);
        connection.Send(new Tversion(NinePConstants.NoTag, 8192, "9P2000"));
        Reply first = await connection.ReceiveAsync();
        Reply second = await connection.ReceiveAsync();
        connection.Send(new Tattach(1, 1, NinePConstants.NoFid, "worker", "runtime"));
        Reply attach = await connection.ReceiveAsync();
        await released;
        return new() { ["replies"] = [first, second], ["attach"] = [attach] };
    };

    [When("the client writes with tag 100 and hangs up while another task releases the write")]
    public void WhenHangUpRacesRelease() => race = async connection =>
    {
        connection.Send(new Twrite(100, 2, 0, new byte[] { 1 }));
        Task released = Task.Run(connection.Release);
        connection.HangUp();
        await released;
        return new();
    };

    [When("the client writes with tag 100 and the connection's lifetime ends while another task releases the write")]
    public void WhenLifetimeRacesRelease() => race = async connection =>
    {
        connection.Send(new Twrite(100, 2, 0, new byte[] { 1 }));
        Task released = Task.Run(connection.Release);
        await Task.Run(connection.EndLifetime);
        await released;
        return new();
    };

    [Then("in every explored schedule the connection answers the write with Rwrite")]
    public void ThenWritten() => Explore((_, replies) => Expect(replies["write"][0], MessageTypes.Rwrite, "the write"));

    [Then("in every explored schedule the connection answers the stat with Rstat")]
    [Then("in every explored schedule the connection answers a stat with tag 100 after Rflush with Rstat")]
    public void ThenStat() => Explore((_, replies) => Expect(replies["stat"][0], MessageTypes.Rstat, "the stat"));

    [Then("in every explored schedule the connection answers the write, if at all, before Rflush")]
    public void ThenFlushAfterWrite() => Explore((_, replies) =>
    {
        string order = string.Join(" ", replies["replies"].Select(reply => reply.Describe()));
        Specification.Assert(order is "Rwrite Rflush" or "Rerror \"unknown\" Rflush" or "Rflush", $"the replies were {order}");
    });

    [Then(@"^in every explored schedule the connection answers the write with Rwrite or Rerror ""interrupted"" before Rversion$")]
    public void ThenWriteBeforeVersion() => Explore((_, replies) =>
    {
        string order = string.Join(" ", replies["replies"].Select(reply => reply.Describe()));
        Specification.Assert(order is "Rwrite Rversion" or "Rerror \"interrupted\" Rversion", $"the replies were {order}");
    });

    [Then("in every explored schedule the client attaches again after Rversion")]
    public void ThenAttached() => Explore((_, replies) => Expect(replies["attach"][0], MessageTypes.Rattach, "the attach"));

    [Then("in every explored schedule no outcome is logged as unknown")]
    public void ThenNothingUnknown() => Explore((connection, _) =>
        Specification.Assert(connection.Unknowns() == 0, "an outcome was logged as unknown"));

    private static void Expect(Reply reply, MessageTypes type, string what)
        => Specification.Assert(reply.Type == type, $"{what} was answered {reply.Describe()}");

    private void Explore(Action<Connection, Dictionary<string, Reply[]>> rule) => Explorer.Explore(
        async () =>
        {
            Connection connection = await Connection.OpenAsync(fixture!, drain, cooperative);
            Dictionary<string, Reply[]> replies = await race!(connection);
            await connection.EndAsync();
            rule(connection, replies);
        },
        output);

    internal readonly record struct Reply(MessageTypes Type, ushort Tag, string? Error)
    {
        internal string Describe() => Error is null ? Type.ToString() : $"Rerror \"{Error}\"";
    }

    private sealed class Connection
    {
        private readonly TaskCompletionSource<uint> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Wire toServer = new();
        private readonly Wire toClient = new();
        private readonly Duplex stream;
        private Task? served;

        private Connection(ControlFixture fixture, TimeSpan drain, bool cooperative)
        {
            var tree = new ProbeTree
            {
                OnOpen = () => new FogOpenFile(write: (_, _, cancellation) =>
                {
                    if (cooperative)
                    {
                        cancellation.Register(() => release.TrySetCanceled(cancellation));
                    }

                    return release.Task;
                }),
            };
            var dispatcher = new FogNinePDispatcher(tree, fixture.NewPolicy(), fixture.Limits with { Drain = drain }, fixture.Time, Logger);
            var processor = new NinePConnectionProcessor(Logger, dispatcher) { Drain = drain };
            var session = new NinePConnectionProcessor.ClientSession();
            session.State = TransportSessionOps.withTransport(session.Dialect, fixture.NodeCertificate, session.State);
            stream = new Duplex(toServer, toClient);
            served = processor.ProcessStreamAsync(stream, null, session, lifetime.Token);
        }

        internal RecordingLogger Logger { get; } = new();

        // Version, attach fid 1 and open "file" for writing as fid 2.
        internal static async Task<Connection> OpenAsync(ControlFixture fixture, TimeSpan drain, bool cooperative)
        {
            var connection = new Connection(fixture, drain, cooperative);
            connection.Send(new Tversion(NinePConstants.NoTag, 8192, "9P2000"));
            await connection.ExpectAsync(MessageTypes.Rversion);
            connection.Send(new Tattach(1, 1, NinePConstants.NoFid, "worker", "runtime"));
            await connection.ExpectAsync(MessageTypes.Rattach);
            connection.Send(new Twalk(2, 1, 2, ["file"]));
            await connection.ExpectAsync(MessageTypes.Rwalk);
            connection.Send(new Topen(3, 2, NinePConstants.OWRITE));
            await connection.ExpectAsync(MessageTypes.Ropen);
            return connection;
        }

        internal void Send(ISerializable message)
        {
            var bytes = new byte[message.Size];
            message.WriteTo(bytes);
            toServer.Write(bytes);
        }

        internal async Task<Reply> ReceiveAsync()
        {
            var header = new byte[NinePConstants.HeaderSize];
            await toClient.ReadExactlyAsync(header);
            var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header) - NinePConstants.HeaderSize];
            await toClient.ReadExactlyAsync(body);
            var type = (MessageTypes)header[4];
            string? error = type == MessageTypes.Rerror
                ? Encoding.UTF8.GetString(body, 2, BinaryPrimitives.ReadUInt16LittleEndian(body))
                : null;
            return new Reply(type, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(5)), error);
        }

        internal void Release() => release.TrySetResult(1);

        internal void HangUp() => toServer.End();

        internal void EndLifetime() => lifetime.Cancel();

        // The client hangs up, and the connection must then end, its lifetime's end included.
        internal async Task EndAsync()
        {
            HangUp();
            try
            {
                await served!;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }

            lifetime.Dispose();
            await stream.DisposeAsync();
        }

        internal int Unknowns() => Logger.At(LogLevel.Warning).Count(message => message.Contains("outcome is unknown", StringComparison.Ordinal));

        private async Task ExpectAsync(MessageTypes type)
        {
            Reply reply = await ReceiveAsync();
            if (reply.Type != type)
            {
                throw new InvalidOperationException($"expected {type}, the connection answered {reply.Describe()}");
            }
        }
    }

    // One direction of the connection: bytes written are read in order, and a read after the end reads 0.
    private sealed class Wire
    {
        private readonly object gate = new();
        private readonly Queue<byte> bytes = new();
        private TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool ended;

        internal void Write(ReadOnlySpan<byte> data)
        {
            TaskCompletionSource signal;
            lock (gate)
            {
                foreach (byte b in data)
                {
                    bytes.Enqueue(b);
                }

                (signal, arrived) = (arrived, new(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            signal.TrySetResult();
        }

        internal void End()
        {
            TaskCompletionSource signal;
            lock (gate)
            {
                ended = true;
                signal = arrived;
            }

            signal.TrySetResult();
        }

        internal async ValueTask<int> ReadAsync(Memory<byte> into, CancellationToken cancellation)
        {
            while (true)
            {
                Task waiting;
                lock (gate)
                {
                    if (bytes.Count > 0)
                    {
                        int n = Math.Min(into.Length, bytes.Count);
                        for (int i = 0; i < n; i++)
                        {
                            into.Span[i] = bytes.Dequeue();
                        }

                        return n;
                    }

                    if (ended)
                    {
                        return 0;
                    }

                    waiting = arrived.Task;
                }

                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellation.Register(() => cancelled.TrySetResult()))
                {
                    await Task.WhenAny(waiting, cancelled.Task);
                }

                cancellation.ThrowIfCancellationRequested();
            }
        }

        internal async Task ReadExactlyAsync(byte[] into)
        {
            for (int at = 0; at < into.Length;)
            {
                int n = await ReadAsync(into.AsMemory(at), CancellationToken.None);
                if (n == 0)
                {
                    throw new EndOfStreamException("the connection ended mid-reply");
                }

                at += n;
            }
        }
    }

    private sealed class Duplex(Wire reading, Wire writing) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => reading.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => reading.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writing.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
