using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Parser;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class DispatcherLifecycleTests
{
    [Fact]
    public async Task DispatcherRejectsMissingArgumentsAndNonRequestMessages()
    {
        using var fixture = new ControlFixture();
        _ = new FogNinePDispatcher(fixture.Tree, fixture.Policy, fixture.Limits with { MessageSize = int.MaxValue });
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Dispatcher.DispatchAsync(string.Empty, NinePMessage.NewMsgTflush(new Tflush(1, 2)), NinePDialect.NineP2000));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Dispatcher.DispatchAsync("s", null!, NinePDialect.NineP2000));
        Error("invalid-request", await fixture.Dispatcher.DispatchAsync("s", NinePMessage.NewMsgRflush(new Rflush(1)), NinePDialect.NineP2000));
    }

    [Fact]
    public async Task DirectoryReadsRespectRecordBoundariesAndCumulativeSnapshotReservations()
    {
        using var fixture = new ControlFixture();
        var tree = new ProbeTree { Listed = [new(2, "a", false), new(3, "bb", false), new(4, "ccc", false)] };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits);
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        var opened = Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(2, 1, NinePConstants.OREAD))));
        Assert.Equal(QidType.QTDIR, opened.Qid.Type);
        byte[] bytes = Assert.IsType<Rread>(await Send(NinePMessage.NewMsgTread(new Tread(3, 1, 0, 245)))).Data.ToArray();
        int first = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes) + 2;
        int second = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(first)) + 2;
        Assert.Equal(bytes[..first], Assert.IsType<Rread>(await Send(NinePMessage.NewMsgTread(new Tread(4, 1, 0, (uint)(first + second - 1))))).Data.ToArray());
        Assert.Equal(bytes.AsSpan(first, second).ToArray(), Assert.IsType<Rread>(await Send(NinePMessage.NewMsgTread(new Tread(5, 1, (ulong)first, (uint)second)))).Data.ToArray());
        Assert.Equal(bytes[(first + second)..], Assert.IsType<Rread>(await Send(NinePMessage.NewMsgTread(new Tread(6, 1, (ulong)(first + second), 245)))).Data.ToArray());
        Error("invalid-request", await Send(NinePMessage.NewMsgTread(new Tread(7, 1, (ulong)first + 1, 245))));
        await dispatcher.CloseSessionAsync("probe");
        dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits with { SnapshotBytesPerSession = first + second - 1 });
        await Initialize(Send);
        Error("snapshot-limit", await Send(NinePMessage.NewMsgTopen(new Topen(8, 1, NinePConstants.OREAD))));
        await dispatcher.CloseSessionAsync("probe");
        dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits with { SnapshotBytesPerSession = bytes.Length * 2 });
        await Initialize(Send);
        Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(9, 1, NinePConstants.OREAD))));

        // Attach additional roots because walking an opened directory is forbidden.
        Assert.IsType<Rattach>(await Send(NinePMessage.NewMsgTattach(new Tattach(10, 2, NinePConstants.NoFid, "worker", "runtime"))));
        Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(11, 2, NinePConstants.OREAD))));
        Assert.IsType<Rattach>(await Send(NinePMessage.NewMsgTattach(new Tattach(12, 3, NinePConstants.NoFid, "worker", "runtime"))));
        Error("snapshot-limit", await Send(NinePMessage.NewMsgTopen(new Topen(13, 3, NinePConstants.OREAD))));
        Assert.IsType<Rclunk>(await Send(NinePMessage.NewMsgTclunk(new Tclunk(14, 1))));
        Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(15, 3, NinePConstants.OREAD))));
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task AdapterMetadataCannotExceedTheNegotiatedResponseFrame()
    {
        using var fixture = new ControlFixture();
        var tree = new ProbeTree { Walked = new(2, new string('x', 300), false) };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits);
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        Error("limit", await Send(NinePMessage.NewMsgTstat(new Tstat(3, 2))));
        tree.Walked = new(3, "file", false);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(4, 1, 3, ["file"])));
        Assert.Equal(0x180U, Assert.IsType<Rstat>(await Send(NinePMessage.NewMsgTstat(new Tstat(5, 3)))).Stat.Mode);
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task SaturatedRequestsKeepOneFlushSlotAndFlushWaitsForTheOldOperation()
    {
        using var fixture = new ControlFixture();
        var finish = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tree = new ProbeTree
        {
            OnOpen = () => new(write: (_, _, token) =>
        {
            token.Register(() => cancelled.TrySetResult());
            return finish.Task;
        }),
        };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits with { RequestsPerSession = 1 });
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE)));
        var write = Send(NinePMessage.NewMsgTwrite(new Twrite(100, 2, 0, new byte[] { 1 })));
        try
        {
            Error("busy", await Send(NinePMessage.NewMsgTstat(new Tstat(100, 1))));
            Error("busy", await Send(NinePMessage.NewMsgTstat(new Tstat(101, 1))));
            var flush = Send(NinePMessage.NewMsgTflush(new Tflush(102, 100)));
            await cancelled.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.False(flush.IsCompleted);

            // Bounded: a second admitted flush would wait on the blocked write instead of failing.
            Error("busy", await Send(NinePMessage.NewMsgTflush(new Tflush(103, 100))).WaitAsync(TimeSpan.FromMilliseconds(250)));
            finish.SetResult(1);
            Assert.Equal(1U, Assert.IsType<Rwrite>(await write.WaitAsync(TimeSpan.FromMilliseconds(250))).Count);
            Assert.IsType<Rflush>(await flush.WaitAsync(TimeSpan.FromMilliseconds(250)));
            Assert.IsType<Rstat>(await Send(NinePMessage.NewMsgTstat(new Tstat(104, 1))));
        }
        finally
        {
            finish.TrySetResult(1);
            await dispatcher.CloseSessionAsync("probe").WaitAsync(TimeSpan.FromMilliseconds(250));
        }
    }

    [Fact]
    public async Task DuplicateTagsAndSelfFlushAreRejectedDirectly()
    {
        using var fixture = new ControlFixture();
        var finish = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tree = new ProbeTree { OnOpen = () => new(write: (_, _, _) => finish.Task) };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits with { RequestsPerSession = 2 });
        Task<object> Send(NinePMessage message) => dispatcher.DispatchAsync("probe", message, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE)));
        Task<object> write = Send(NinePMessage.NewMsgTwrite(new Twrite(100, 2, 0, new byte[] { 1 })));
        CancellationTokenSource pendingCancellation = PendingCancellation(dispatcher, "probe", 100);
        try
        {
            Error("busy", await Send(NinePMessage.NewMsgTstat(new Tstat(100, 1))).WaitAsync(TimeSpan.FromMilliseconds(250)));
            Error("invalid-request", await Send(NinePMessage.NewMsgTflush(new Tflush(101, 101))).WaitAsync(TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            finish.TrySetResult(1);
            await write.WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.Throws<ObjectDisposedException>(pendingCancellation.Cancel);
            await dispatcher.CloseSessionAsync("probe").WaitAsync(TimeSpan.FromMilliseconds(250));
        }
    }

    [Fact]
    public async Task PartialWalkStopsAtTheFirstMissingElement()
    {
        using var fixture = new ControlFixture();
        var tree = new ProbeTree();
        tree.RejectedNames.Add("missing");
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits);
        Task<object> Send(NinePMessage message) => dispatcher.DispatchAsync("probe", message, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);

        var walk = Assert.IsType<Rwalk>(await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, [".", "missing", "."]))));

        Assert.Single(walk.Wqid);
        Error("invalid-request", await Send(NinePMessage.NewMsgTstat(new Tstat(3, 2))));
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task InFlightWritesRejectConcurrentWriteAndClunkAndRecheckRevocation()
    {
        using var fixture = new ControlFixture();
        var finish = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tree = new ProbeTree { OnOpen = () => new(write: (_, _, _) => finish.Task) };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits);
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE)));
        var write = Send(NinePMessage.NewMsgTwrite(new Twrite(100, 2, 0, new byte[] { 1 })));
        try
        {
            Error("busy", await Send(NinePMessage.NewMsgTwrite(new Twrite(101, 2, 0, new byte[] { 2 }))).WaitAsync(TimeSpan.FromMilliseconds(250)));
            Error("busy", await Send(NinePMessage.NewMsgTclunk(new Tclunk(102, 2))).WaitAsync(TimeSpan.FromMilliseconds(250)));
            fixture.Policy.Replace(2, []);
            finish.SetResult(1);
            Error("denied", await write.WaitAsync(TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            finish.TrySetResult(1);
            await dispatcher.CloseSessionAsync("probe").WaitAsync(TimeSpan.FromMilliseconds(250));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionAndTerminalCloseCancelPendingWaitsAndDisposeOpenFiles(bool terminal)
    {
        using var fixture = new ControlFixture();
        int disposed = 0;
        var tree = new ProbeTree
        {
            OnOpen = () => new(
                write: async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 1;
        },
                close: _ => disposed++),
        };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits);
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE)));
        var write = Send(NinePMessage.NewMsgTwrite(new Twrite(100, 2, 0, new byte[] { 1 })));
        Task reset = terminal ? dispatcher.CloseSessionAsync("probe") : Send(NinePMessage.NewMsgTversion(new Tversion(65535, 256, "9P2000")));
        await reset.WaitAsync(TimeSpan.FromMilliseconds(250));
        Error("interrupted", await write.WaitAsync(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(1, disposed);
        Assert.Equal(2, tree.ClosedSessions.Count);
        Assert.All(tree.ClosedSessions, id => Assert.Equal("probe", id));
        Error(terminal ? "not-ready" : "invalid-request", await Send(NinePMessage.NewMsgTread(new Tread(101, 2, 0, 1))));
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task VersionResetRejectsNewRequestsWhileAnOldRequestDrains()
    {
        using var fixture = new ControlFixture();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tree = new ProbeTree
        {
            OnOpen = () => new(write: async (_, _, token) =>
            {
                token.Register(canceled.SetResult);
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return 1;
            }),
        };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits);
        Task<object> Send(NinePMessage message) => dispatcher.DispatchAsync("probe", message, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE)));
        Task<object> write = Send(NinePMessage.NewMsgTwrite(new Twrite(4, 2, 0, new byte[] { 1 })));
        Task<object> reset = Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 256, "9P2000")));
        await canceled.Task.WaitAsync(TimeSpan.FromMilliseconds(250));

        Error("not-ready", await Send(NinePMessage.NewMsgTstat(new Tstat(5, 1))).WaitAsync(TimeSpan.FromMilliseconds(250)));
        finish.SetResult(1);
        Error("interrupted", await write.WaitAsync(TimeSpan.FromMilliseconds(250)));
        Assert.IsType<Rversion>(await reset.WaitAsync(TimeSpan.FromMilliseconds(250)));
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task AnOldVersionResetCannotReplaceANewerSessionWithTheSameId()
    {
        using var fixture = new ControlFixture();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tree = new ProbeTree
        {
            OnOpen = () => new(write: async (_, _, token) =>
            {
                token.Register(canceled.SetResult);
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return 1;
            }),
        };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits);
        Task<object> Send(NinePMessage message) => dispatcher.DispatchAsync("probe", message, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE)));
        Task<object> write = Send(NinePMessage.NewMsgTwrite(new Twrite(4, 2, 0, new byte[] { 1 })));
        Task<object> oldVersion = Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 256, "9P2000")));
        await canceled.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
        Task close = dispatcher.CloseSessionAsync("probe");
        Assert.IsType<Rversion>(await Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 256, "9P2000"))));

        finish.SetResult();
        Error("interrupted", await write.WaitAsync(TimeSpan.FromMilliseconds(250)));
        Error("not-ready", await oldVersion.WaitAsync(TimeSpan.FromMilliseconds(250)));
        await close.WaitAsync(TimeSpan.FromMilliseconds(250));
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task VersionResetFailsWhenItsSessionIsClosedWhileDraining()
    {
        using var fixture = new ControlFixture();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tree = new ProbeTree
        {
            OnOpen = () => new(write: async (_, _, token) =>
            {
                token.Register(canceled.SetResult);
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return 1;
            }),
        };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits);
        Task<object> Send(NinePMessage message) => dispatcher.DispatchAsync("probe", message, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE)));
        Task<object> write = Send(NinePMessage.NewMsgTwrite(new Twrite(4, 2, 0, new byte[] { 1 })));
        Task<object> version = Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 256, "9P2000")));
        await canceled.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
        Task close = dispatcher.CloseSessionAsync("probe");

        finish.SetResult();
        Error("interrupted", await write.WaitAsync(TimeSpan.FromMilliseconds(250)));
        Error("not-ready", await version.WaitAsync(TimeSpan.FromMilliseconds(250)));
        await close.WaitAsync(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task SnapshotChecksEnforceAdapterBudgetsDisposeExpiredBytesAndBoundReads()
    {
        using var fixture = new ControlFixture();
        int disposed = 0;
        var tree = new ProbeTree { OnOpen = () => new(new byte[300], close: _ => disposed++) };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits with { SnapshotBytesPerSession = 300, MessageSize = 256 }, fixture.Time);
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"])));
        var open = Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OREAD))));
        Assert.Equal(232U, open.Iounit);
        Assert.Equal(QidType.QTFILE, open.Qid.Type);
        Error("busy", await Send(NinePMessage.NewMsgTopen(new Topen(4, 2, NinePConstants.OREAD))));
        Assert.Equal(245U, Assert.IsType<Rread>(await Send(NinePMessage.NewMsgTread(new Tread(5, 2, 0, uint.MaxValue)))).Count);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(6, 1, 3, ["file"])));
        Error("snapshot-limit", await Send(NinePMessage.NewMsgTopen(new Topen(7, 3, NinePConstants.OREAD))));
        Assert.Equal(1, disposed);
        fixture.Time.Advance(fixture.Limits.SnapshotLifetime);
        Error("tx-expired", await Send(NinePMessage.NewMsgTread(new Tread(8, 2, 0, 1))));
        Assert.Equal(2, disposed);
        Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(9, 3, NinePConstants.OREAD))));
        Assert.IsType<Rclunk>(await Send(NinePMessage.NewMsgTclunk(new Tclunk(10, 3))));
        Assert.Equal(4, disposed); // Clunk callback then disposal callback.
        Error("invalid-request", await Send(NinePMessage.NewMsgTread(new Tread(11, 3, 0, 1))));
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task ExactFrameAndSessionLifetimeBoundariesAndAdapterFailuresAreExplicit()
    {
        using var fixture = new ControlFixture();
        var tree = new ProbeTree { OnOpen = () => new(write: (_, bytes, _) => Task.FromResult((uint)bytes.Length)) };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits with { MessageSize = 256 }, fixture.Time);
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, Enumerable.Repeat(".", 16).ToArray())));
        Assert.IsType<Rstat>(await Send(NinePMessage.NewMsgTstat(new Tstat(3, 2))));
        await Send(NinePMessage.NewMsgTwalk(new Twalk(4, 1, 3, ["file"])));
        await Send(NinePMessage.NewMsgTopen(new Topen(5, 3, NinePConstants.OWRITE)));
        Assert.Equal(233U, Assert.IsType<Rwrite>(await Send(NinePMessage.NewMsgTwrite(new Twrite(6, 3, 0, new byte[233])))).Count);
        tree.OnOpen = () => throw new InvalidOperationException("private details");
        await Send(NinePMessage.NewMsgTwalk(new Twalk(7, 1, 4, ["file"])));
        Error("unavailable", await Send(NinePMessage.NewMsgTopen(new Topen(8, 4, NinePConstants.OREAD))));
        fixture.Time.Advance(fixture.Limits.SessionLifetime);
        Error("denied", await Send(NinePMessage.NewMsgTstat(new Tstat(9, 1))));
        Error("denied", await Send(NinePMessage.NewMsgTversion(new Tversion(65535, 256, "9P2000"))));
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task UnsupportedRequestsAreDeniedWithTheirTagAndLeaveTheFidUsable()
    {
        using var fixture = new ControlFixture();
        int opens = 0;
        var tree = new ProbeTree
        {
            OnOpen = () =>
        {
            opens++;
            return new();
        },
        };
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits, fixture.Time);
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        await Initialize(Send);
        var stat = new Stat(0, 0, 0, new Qid(QidType.QTFILE, 0, 2), 0, 0, 0, 0, "renamed", "fog", "fog", "fog");
        foreach (var (tag, message) in new (ushort, NinePMessage)[]
        {
            (20, NinePMessage.NewMsgTauth(new Tauth(20, 1, "worker", "runtime"))),
            (21, NinePMessage.NewMsgTcreate(new Tcreate(21, 1, "created", 0x1A4, NinePConstants.OWRITE))),
            (22, NinePMessage.NewMsgTwstat(new Twstat(22, 1, stat))),
            (23, NinePMessage.NewMsgTremove(new Tremove(23, 1))),
        })
        {
            var error = Assert.IsType<Rerror>(await Send(message));
            Assert.Equal(("denied", tag), (error.Ename, error.Tag));
        }

        Assert.Equal(0, opens);
        Assert.Equal("/", Assert.IsType<Rstat>(await Send(NinePMessage.NewMsgTstat(new Tstat(24, 1)))).Stat.Name);
        await dispatcher.CloseSessionAsync("probe");
    }

    [Fact]
    public async Task StatReportsTheOpenedSnapshotLengthAndClunkReleasesAnUnopenedFid()
    {
        using var fixture = new ControlFixture();
        var tree = new ProbeTree();
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits, fixture.Time);
        Task<object> Send(NinePMessage m) => dispatcher.DispatchAsync("probe", m, NinePDialect.NineP2000, fixture.NodeCertificate);
        ulong Length(object reply) => Assert.IsType<Rstat>(reply).Stat.Length;
        await Initialize(Send);
        Assert.IsType<Rwalk>(await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"]))));
        Assert.Equal(0UL, Length(await Send(NinePMessage.NewMsgTstat(new Tstat(3, 2)))));
        Assert.IsType<Rclunk>(await Send(NinePMessage.NewMsgTclunk(new Tclunk(4, 2))));
        Error("invalid-request", await Send(NinePMessage.NewMsgTstat(new Tstat(5, 2))));

        tree.OnOpen = () => new(write: (_, bytes, _) => Task.FromResult((uint)bytes.Length));
        Assert.IsType<Rwalk>(await Send(NinePMessage.NewMsgTwalk(new Twalk(6, 1, 3, ["file"]))));
        Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(7, 3, NinePConstants.OWRITE))));
        Assert.Equal(0UL, Length(await Send(NinePMessage.NewMsgTstat(new Tstat(8, 3)))));

        tree.OnOpen = () => new([1, 2, 3, 4, 5]);
        Assert.IsType<Rwalk>(await Send(NinePMessage.NewMsgTwalk(new Twalk(9, 1, 4, ["file"]))));
        Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(10, 4, NinePConstants.OREAD))));
        Assert.Equal(5UL, Length(await Send(NinePMessage.NewMsgTstat(new Tstat(11, 4)))));
        await dispatcher.CloseSessionAsync("probe");
    }

    private static CancellationTokenSource PendingCancellation(FogNinePDispatcher dispatcher, string sessionId, ushort tag)
    {
        var sessions = (System.Collections.IDictionary)typeof(FogNinePDispatcher)
            .GetField("sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(dispatcher)!;
        object session = sessions[sessionId]!;
        var pending = (System.Collections.IDictionary)session.GetType()
            .GetProperty("Pending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(session)!;
        object operation = pending[tag]!;
        return (CancellationTokenSource)operation.GetType()
            .GetProperty("Cancellation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(operation)!;
    }

    private static async Task Initialize(Func<NinePMessage, Task<object>> send)
    {
        Assert.IsType<Rversion>(await send(NinePMessage.NewMsgTversion(new Tversion(65535, 256, "9P2000"))));
        Assert.IsType<Rattach>(await send(NinePMessage.NewMsgTattach(new Tattach(1, 1, NinePConstants.NoFid, "worker", "runtime"))));
    }

    private static void Error(string code, object result) => Assert.Equal(code, Assert.IsType<Rerror>(result).Ename);

    private sealed class ProbeTree : FogFileTree
    {
        public override FogFileNode Root { get; } = new(1, "/", true);

        internal FogFileNode Walked { get; set; } = new(2, "file", false);

        internal IReadOnlyList<FogFileNode>? Listed { get; set; }

        internal Func<FogOpenFile> OnOpen { get; set; } = () => new();

        internal HashSet<string> RejectedNames { get; } = [];

        internal List<string> ClosedSessions { get; } = new();

        public override FogFileNode Walk(FogPrincipal principal, FogFileNode directory, string name) =>
            RejectedNames.Contains(name) ? throw new FogException("tx-expired") : name == "." ? directory : Walked;

        public override IReadOnlyList<FogFileNode> List(FogPrincipal principal, FogFileNode directory) => Listed ?? [Walked];

        public override void Check(FogPrincipal principal, FogFileNode node)
        {
        }

        public override FogOpenFile Open(FogPrincipal principal, string session, FogFileNode node, byte mode, long snapshotBudget) => OnOpen();

        public override void CloseSession(string session) => ClosedSessions.Add(session);
    }
}
