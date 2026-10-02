using System.Text;
using FsCheck.Xunit;
using NinePSharp.Constants;
using NinePSharp.Messages;
using NinePSharp.Parser;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class ControlDispatcherTests
{
    // Version, attach fid 1, then clone one transaction: random suffixes start from a live session, not from not-ready.
    private static readonly byte[] LiveSession = [0, 0, 2, 0, 3, 1, 0, 0, 0, 4, 1, 2, 3, 2, 3, 4, 5, 2, 0, 6, 2, 0, 4, 8, 2];

    [Fact]
    public async Task StandardFilesCommitOnceAndReturnImmutableResults()
    {
        using var fixture = new ControlFixture();
        await fixture.Initialize();
        string id = await fixture.Clone();
        await fixture.Upload(id, [1, 2, 3]);
        Assert.Equal(0, fixture.Effects);
        Assert.Equal(7U, Assert.IsType<Rwrite>(await fixture.Write(4, "commit\n"u8.ToArray())).Count);
        Assert.Equal(7U, Assert.IsType<Rwrite>(await fixture.Write(4, "commit\n"u8.ToArray())).Count);
        Assert.Equal(1, fixture.Effects);
        Assert.Equal(4, Assert.IsType<Rwalk>(await fixture.Walk(1, 5, "control", "fixture", id, "reply")).Wqid.Length);
        Assert.IsType<Ropen>(await fixture.Open(5, NinePConstants.OREAD));
        Assert.Equal(new byte[] { 2, 3 }, Assert.IsType<Rread>(await fixture.Read(5, 1, uint.MaxValue)).Data.ToArray());
        Assert.Empty(Assert.IsType<Rread>(await fixture.Read(5, ulong.MaxValue)).Data.ToArray());
        Assert.Equal(8U, Assert.IsType<Rwrite>(await fixture.Write(4, "release\n"u8.ToArray())).Count);
        Assert.Equal("tx-expired", Assert.IsType<Rerror>(await fixture.Read(5)).Ename);
        Assert.IsType<Rclunk>(await fixture.Clunk(5));
    }

    [Fact]
    public async Task MissingWalksNeverAllocateAndPartialWalkDoesNotInstallNewfid()
    {
        using var fixture = new ControlFixture();
        await fixture.Initialize();
        Assert.IsType<Rerror>(await fixture.Walk(1, 2, "missing"));
        var partial = Assert.IsType<Rwalk>(await fixture.Walk(1, 2, "control", "fixture", "invented"));
        Assert.Equal(2, partial.Wqid.Length);
        Assert.IsType<Rerror>(await fixture.Open(2, NinePConstants.OREAD));
        Assert.Empty(fixture.Store.LiveIds());
        Assert.Empty(Assert.IsType<Rwalk>(await fixture.Walk(1, 2)).Wqid);
        Assert.IsType<Ropen>(await fixture.Open(2, NinePConstants.OREAD));
        Assert.IsType<Rerror>(await fixture.Walk(2, 3, "control"));
    }

    [Fact]
    public async Task UnsealedReplacementAndSessionResetCannotExecutePartialInput()
    {
        using var fixture = new ControlFixture();
        await fixture.Initialize();
        string id = await fixture.Clone();
        await fixture.Upload(id, [7]);
        Assert.IsType<Rwalk>(await fixture.Walk(1, 5, "control", "fixture", id, "request"));
        Assert.IsType<Ropen>(await fixture.Open(5, NinePConstants.OWRITE));
        Assert.IsType<Rwrite>(await fixture.Write(5, [9]));
        Assert.Equal("upload-open", Assert.IsType<Rerror>(await fixture.Write(4, "commit\n"u8.ToArray())).Ename);
        await fixture.Initialize();
        Assert.IsType<Rerror>(await fixture.Read(5));
        await fixture.Walk(1, 4, "control", "fixture", id, "ctl");
        await fixture.Open(4, NinePConstants.OWRITE);
        Assert.IsType<Rwrite>(await fixture.Write(4, "commit\n"u8.ToArray()));
        Assert.Equal(new byte[] { 7 }, fixture.Store.ReadOutput(fixture.Owner, id, "reply", 0, 8));
    }

    [Fact]
    public async Task FlushCancelsOnlyTheWaitAndRetainsTheAcceptedCommit()
    {
        using var fixture = new ControlFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Apply = () =>
        {
            entered.TrySetResult();
            return finish.Task;
        };
        await fixture.Initialize();
        string id = await fixture.Clone();
        await fixture.Upload(id, [1]);
        Task<object> write = fixture.Send(NinePMessage.NewMsgTwrite(new Twrite(100, 4, 0, "commit\n"u8.ToArray())));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
            Task<object> flush = fixture.Send(NinePMessage.NewMsgTflush(new Tflush(101, 100)));
            Assert.Equal("interrupted", Assert.IsType<Rerror>(await write.WaitAsync(TimeSpan.FromMilliseconds(250))).Ename);
            Assert.IsType<Rflush>(await flush.WaitAsync(TimeSpan.FromMilliseconds(250)));
            Assert.Equal("committing", fixture.Store.Status(fixture.Owner, id).State);
            finish.SetResult();
            Assert.IsType<Rwrite>(await fixture.Write(4, "commit\n"u8.ToArray()).WaitAsync(TimeSpan.FromMilliseconds(250)));
            Assert.Equal(1, fixture.Effects);
        }
        finally
        {
            finish.TrySetResult();
            await fixture.Dispatcher.CloseSessionAsync("s").WaitAsync(TimeSpan.FromMilliseconds(250));
        }
    }

    [Fact]
    public async Task AttachedRootsRecheckPolicyAndCannotChangeIdentity()
    {
        using var fixture = new ControlFixture();
        await fixture.Initialize();
        string id = await fixture.Clone();
        var wrong = new Tattach(20, 6, NinePConstants.NoFid, "other", "runtime");
        Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Send(NinePMessage.NewMsgTattach(wrong))).Ename);
        fixture.Policy.Replace(2, []);
        Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Walk(1, 6, "control", "fixture", id)).Ename);
        Assert.IsType<Rerror>(await fixture.Clunk(1));
        Assert.IsType<Rerror>(await fixture.Read(1));
    }

    [Fact]
    public async Task AuthenticationRequiresCertificateExactExportAndNoAfid()
    {
        using var fixture = new ControlFixture();
        _ = await fixture.Send(NinePMessage.NewMsgTversion(new Tversion(65535, 512, "9P2000")));
        foreach (var request in new[]
        {
            new Tattach(1, 1, NinePConstants.NoFid, "worker", "fog"),
            new Tattach(2, 1, 10, "worker", "runtime"),
            new Tattach(3, 1, NinePConstants.NoFid, "unknown", "runtime"),
        })
        {
            Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Send(NinePMessage.NewMsgTattach(request))).Ename);
        }

        object reply = await fixture.Dispatcher.DispatchAsync(
            "s",
            NinePMessage.NewMsgTattach(new Tattach(4, 1, NinePConstants.NoFid, "worker", "runtime")),
            NinePDialect.NineP2000);
        Assert.Equal("denied", Assert.IsType<Rerror>(reply).Ename);
    }

    [Fact]
    public async Task DirectoryReadsReturnWholeStatsAndStableQids()
    {
        using var fixture = new ControlFixture();
        await fixture.Initialize();
        var walked = Assert.IsType<Rwalk>(await fixture.Walk(1, 2, "control", "fixture"));
        Assert.Equal(walked.Wqid[^1], Assert.IsType<Ropen>(await fixture.Open(2, NinePConstants.OREAD)).Qid);
        var tiny = Assert.IsType<Rread>(await fixture.Read(2, 0, 1));
        Assert.Empty(tiny.Data.ToArray());
        var full = Assert.IsType<Rread>(await fixture.Read(2));
        int offset = 0;
        Assert.Equal("clone", new Stat(full.Data.Span, ref offset).Name);
        Assert.Equal(full.Data.Length, offset);
        Assert.IsType<Rerror>(await fixture.Read(2, 1));
        await fixture.Walk(1, 3, "..", "control", ".", "fixture", "..");
        Assert.Equal("control", Assert.IsType<Rstat>(await fixture.Send(NinePMessage.NewMsgTstat(new Tstat(90, 3)))).Stat.Name);
    }

    [Fact]
    public async Task SnapshotAndSessionDeadlinesDoNotResetWithActivityOrVersion()
    {
        using var fixture = new ControlFixture();
        await fixture.Initialize();
        await fixture.Walk(1, 2, "control");
        await fixture.Open(2, NinePConstants.OREAD);
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("tx-expired", Assert.IsType<Rerror>(await fixture.Read(2)).Ename);
        Assert.IsType<Rerror>(await fixture.Open(2, NinePConstants.OREAD));
        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.IsType<Rerror>(await fixture.Read(1));
        Assert.IsType<Rerror>(await fixture.Send(NinePMessage.NewMsgTversion(new Tversion(65535, 4096, "9P2000"))));
    }

    [Property(MaxTest = 300)]
    public void GeneratedRequestSequencesAgreeWithTheSessionModel(byte[] commands) => NinePSharp.Fuzzer.FogDispatcherFuzz.Run(commands);

    [Property(MaxTest = 500)]
    public void GeneratedRequestsInALiveSessionAgreeWithTheSessionModel(byte[] commands)
        => NinePSharp.Fuzzer.FogDispatcherFuzz.Run([.. LiveSession, .. commands]);

    [Property(MaxTest = 100)]
    public void GeneratedUploadsCommitOnceThroughTheControlFiles(byte[] input) => NinePSharp.Fuzzer.FogFileFuzz.Run(input);

    [Fact]
    public void DispatcherFuzzSeedsReachCommitsEffectsAndRejections()
    {
        string corpus = Path.Combine(RepositoryRoot(), "corpus", "fog-dispatcher");
        var (commits, effects) = NinePSharp.Fuzzer.FogDispatcherFuzz.Execute(File.ReadAllBytes(Path.Combine(corpus, "session.bin")));
        Assert.Equal((2, 1), (commits, effects));
        foreach (string seed in Directory.GetFiles(corpus))
        {
            using var stream = File.OpenRead(seed);
            NinePSharp.Fuzzer.FogDispatcherFuzz.Run(stream);
        }

        NinePSharp.Fuzzer.FogDispatcherFuzz.Run(new byte[8192]);
        NinePSharp.Fuzzer.FogFileFuzz.Run([0]);
        NinePSharp.Fuzzer.FogFileFuzz.Run([1, 2, 3, 4]);
    }

    [Property(MaxTest = 100)]
    public bool GeneratedPayloadFragmentsHaveOneWireCommitEffect(byte[] input, byte stride)
    {
        using var fixture = new ControlFixture();
        fixture.Initialize().GetAwaiter().GetResult();
        string id = fixture.Clone().GetAwaiter().GetResult();
        byte[] bytes = input.Take(256).ToArray();
        _ = fixture.Walk(1, 3, "control", "fixture", id, "request").GetAwaiter().GetResult();
        _ = fixture.Open(3, NinePConstants.OWRITE).GetAwaiter().GetResult();
        for (int offset = 0; offset < bytes.Length; offset += stride + 1)
        {
            Assert.IsType<Rwrite>(fixture.Write(3, bytes.Skip(offset).Take(stride + 1).ToArray(), (ulong)offset).GetAwaiter().GetResult());
        }

        Assert.Equal(0, fixture.Effects);
        _ = fixture.Clunk(3).GetAwaiter().GetResult();
        _ = fixture.Walk(1, 4, "control", "fixture", id, "ctl").GetAwaiter().GetResult();
        _ = fixture.Open(4, NinePConstants.OWRITE).GetAwaiter().GetResult();
        Assert.IsType<Rwrite>(fixture.Write(4, "commit\n"u8.ToArray()).GetAwaiter().GetResult());
        return fixture.Effects == 1 && fixture.Store.ReadOutput(fixture.Owner, id, "reply", 0, 4096).SequenceEqual(bytes);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NinePSharp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("NinePSharp.sln");
    }
}
