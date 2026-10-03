using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;
using Xunit;

namespace NinePSharp.Fog.Namespaces.Tests;

public sealed class BoundedNamespaceExportTests
{
    private static readonly FogNamespaceLimits Limits = new(
        Sessions: 2,
        FidsPerSession: 3,
        RequestsPerSession: 1,
        MessageSize: 512,
        SessionLifetime: TimeSpan.FromMinutes(1));

    [Fact]
    public void EveryLimitMustBePositiveAndTheMessageSizeUsable()
    {
        var inner = new FakeDispatcher();
        foreach (FogNamespaceLimits invalid in new[]
        {
            Limits with { Sessions = 0 }, Limits with { FidsPerSession = 0 }, Limits with { RequestsPerSession = 0 },
            Limits with { MessageSize = 255 }, Limits with { MessageSize = (uint)int.MaxValue + 1 }, Limits with { SessionLifetime = TimeSpan.Zero },
        })
        {
            Assert.Equal("limits", Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedNamespaceExport(inner, invalid)).ParamName);
        }

        Assert.Equal("inner", Assert.Throws<ArgumentNullException>(() => new BoundedNamespaceExport(null!, Limits)).ParamName);
        _ = new BoundedNamespaceExport(inner, Limits with { MessageSize = 256 });
        _ = new BoundedNamespaceExport(inner, Limits with { MessageSize = int.MaxValue });
        Assert.Equal("limits", Assert.Throws<ArgumentNullException>(() => new BoundedNamespaceExport(inner, null!)).ParamName);
    }

    [Fact]
    public async Task SessionsAreCountedUntilClosedAndVersionClampsTheMessageSize()
    {
        var inner = new FakeDispatcher();
        var export = new BoundedNamespaceExport(inner, Limits);
        Assert.IsType<Rversion>(await Version(export, "a", 65535));
        Assert.Equal(512U, inner.LastVersionSize);
        Assert.IsType<Rversion>(await Version(export, "b"));
        Assert.Equal("limit", Error(await Version(export, "c")));
        Assert.IsType<Rversion>(await Version(export, "a"));
        await export.CloseSessionAsync("b");
        Assert.Equal(new[] { "b" }, inner.Closed);
        Assert.IsType<Rversion>(await Version(export, "c"));
        await export.CloseSessionAsync("never");
        Assert.Equal(new[] { "b" }, inner.Closed);
    }

    [Fact]
    public async Task OnlyRequestsAreAdmittedAndAnExactFrameFits()
    {
        var inner = new FakeDispatcher();
        var export = new BoundedNamespaceExport(inner, Limits);
        var notARequest = Assert.IsType<Rerror>(await Send(export, "a", NinePMessage.NewMsgRflush(new Rflush(1))));
        Assert.Equal(("invalid-request", NinePConstants.NoTag), (notARequest.Ename, notARequest.Tag));
        await Version(export, "a", 256);
        Assert.IsType<Rwrite>(await Send(export, "a", NinePMessage.NewMsgTwrite(new Twrite(2, 1, 0, new byte[233]))));
        Assert.Equal(1, inner.Forwarded(typeof(Twrite)));
    }

    [Fact]
    public async Task EveryClassicRequestTypeIsBoundedAndForwarded()
    {
        var inner = new FakeDispatcher();
        var export = new BoundedNamespaceExport(inner, Limits);
        await Version(export, "a");
        var stat = new Stat(0, 0, 0, new Qid(QidType.QTFILE, 0, 1), 0, 0, 0, 0, "x", "u", "g", "m");
        foreach (NinePMessage request in new[]
        {
            NinePMessage.NewMsgTauth(new Tauth(1, 9, "worker", "/")),
            NinePMessage.NewMsgTopen(new Topen(2, 1, NinePConstants.OREAD)),
            NinePMessage.NewMsgTcreate(new Tcreate(3, 1, "x", 0x1A4, NinePConstants.OWRITE)),
            NinePMessage.NewMsgTread(new Tread(4, 1, 0, 8)),
            NinePMessage.NewMsgTstat(new Tstat(5, 1)),
            NinePMessage.NewMsgTwstat(new Twstat(6, 1, stat)),
        })
        {
            Assert.IsType<Rerror>(await Send(export, "a", request));
        }

        foreach (Type type in new[] { typeof(Tauth), typeof(Topen), typeof(Tcreate), typeof(Tread), typeof(Tstat), typeof(Twstat) })
        {
            Assert.Equal(1, inner.Forwarded(type));
        }
    }

    [Fact]
    public async Task ANegotiatedSizeLargerThanRequestedIsClamped()
    {
        var inner = new FakeDispatcher { ReportedSize = 65535 };
        var export = new BoundedNamespaceExport(inner, Limits);
        Assert.Equal(65535U, Assert.IsType<Rversion>(await Version(export, "a", 300)).MSize);
        Assert.Equal("invalid-request", Error(await Send(export, "a", NinePMessage.NewMsgTwrite(new Twrite(2, 1, 0, new byte[300])))));
    }

    [Fact]
    public async Task AFlushHoldsOnlyItsReservedSlot()
    {
        var inner = new FakeDispatcher();
        var export = new BoundedNamespaceExport(inner, Limits with { RequestsPerSession = 2 });
        await Version(export, "a");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Gate = gate.Task;
        Task<object> blocked = Send(export, "a", Attach(1, 1));
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<object> flush = Send(export, "a", NinePMessage.NewMsgTflush(new Tflush(2, 1)));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(3, 1, 2, "x")).WaitAsync(TimeSpan.FromSeconds(1)));
        gate.SetResult();
        await blocked.WaitAsync(TimeSpan.FromSeconds(1));
        await flush.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RequestsNeedAVersionAWholeFrameAndALiveSession()
    {
        var inner = new FakeDispatcher();
        var time = new ManualTime();
        var export = new BoundedNamespaceExport(inner, Limits, time);
        Assert.Equal("not-ready", Error(await Send(export, "a", Attach(1, 1))));
        await Version(export, "a", 256);
        Assert.Equal("invalid-request", Error(await Send(export, "a", NinePMessage.NewMsgTwrite(new Twrite(2, 1, 0, new byte[256])))));
        Assert.Equal("invalid-request", Error(await Send(export, "a", NinePMessage.NewMsgTclunk(new Tclunk(NinePConstants.NoTag, 1)))));
        Assert.IsType<Rattach>(await Send(export, "a", Attach(3, 1)));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.IsType<Rversion>(await Version(export, "a"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("denied", Error(await Send(export, "a", Attach(4, 1))));
        Assert.Equal("denied", Error(await Version(export, "a")));
        Assert.Equal(1, inner.Forwarded(typeof(Tattach)));
    }

    [Fact]
    public async Task FidsAreCountedThroughAttachWalkClunkAndRemove()
    {
        var inner = new FakeDispatcher();
        var export = new BoundedNamespaceExport(inner, Limits);
        await Version(export, "a");
        Assert.IsType<Rattach>(await Send(export, "a", Attach(1, 1)));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(2, 1, 2, "x")));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(3, 1, 1, "x")));
        inner.PartialWalks = true;
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(4, 1, 3, "x", "y")));
        inner.PartialWalks = false;
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(5, 1, 3, "x")));
        Assert.Equal("limit", Error(await Send(export, "a", Walk(6, 1, 4, "x"))));
        Assert.Equal("limit", Error(await Send(export, "a", Attach(7, 5))));
        Assert.Equal(1, inner.Forwarded(typeof(Tattach)));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(8, 2, 2, "x")));

        inner.FailClunk = true;
        Assert.IsType<Rerror>(await Send(export, "a", NinePMessage.NewMsgTclunk(new Tclunk(9, 3))));
        Assert.Equal("limit", Error(await Send(export, "a", Walk(10, 1, 4, "x"))));
        inner.FailClunk = false;
        Assert.IsType<Rclunk>(await Send(export, "a", NinePMessage.NewMsgTclunk(new Tclunk(11, 3))));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(12, 1, 4, "x")));
        Assert.IsType<Rerror>(await Send(export, "a", NinePMessage.NewMsgTremove(new Tremove(13, 4))));
        Assert.IsType<Rattach>(await Send(export, "a", Attach(14, 5)));

        await Version(export, "a");
        Assert.IsType<Rattach>(await Send(export, "a", Attach(15, 1)));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(16, 1, 2, "x")));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(17, 1, 3, "x")));
    }

    // lib9p's sauth allocates the afid from the same fid pool as attach and walk.
    [Fact]
    public async Task AfidsAreCountedAsFidsOnceAuthAnswers()
    {
        var inner = new FakeDispatcher();
        var export = new BoundedNamespaceExport(inner, Limits);
        await Version(export, "a");
        Assert.IsType<Rerror>(await Send(export, "a", Auth(1, 1)));
        inner.AcceptAuth = true;
        Assert.IsType<Rauth>(await Send(export, "a", Auth(2, 1)));
        Assert.IsType<Rattach>(await Send(export, "a", Attach(3, 2)));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(4, 2, 3, "x")));
        Assert.Equal("limit", Error(await Send(export, "a", Auth(5, 4))));
        Assert.Equal("limit", Error(await Send(export, "a", Walk(6, 2, 4, "x"))));
        Assert.Equal(2, inner.Forwarded(typeof(Tauth)));
        Assert.IsType<Rclunk>(await Send(export, "a", NinePMessage.NewMsgTclunk(new Tclunk(7, 1))));
        Assert.IsType<Rauth>(await Send(export, "a", Auth(8, 4)));
    }

    [Fact]
    public async Task OneRequestIsAdmittedAtATimeButAFlushStillHasItsSlot()
    {
        var inner = new FakeDispatcher();
        var export = new BoundedNamespaceExport(inner, Limits);
        await Version(export, "a");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Gate = gate.Task;
        Task<object> blocked = Send(export, "a", Attach(1, 1));
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("busy", Error(await Send(export, "a", Walk(2, 1, 2, "x"))));
        Task<object> flush = Send(export, "a", NinePMessage.NewMsgTflush(new Tflush(3, 1)));

        // Bounded: a second admitted flush would wait on the blocked request instead of failing.
        Assert.Equal("busy", Error(await Send(export, "a", NinePMessage.NewMsgTflush(new Tflush(4, 1))).WaitAsync(TimeSpan.FromSeconds(1))));
        gate.SetResult();
        Assert.IsType<Rattach>(await blocked.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsType<Rflush>(await flush.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsType<Rwalk>(await Send(export, "a", Walk(5, 1, 2, "x")));
        Assert.IsType<Rflush>(await Send(export, "a", NinePMessage.NewMsgTflush(new Tflush(6, 5))).WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task InnerFailuresReleaseTheirRequestSlot()
    {
        var inner = new FakeDispatcher();
        var export = new BoundedNamespaceExport(inner, Limits);
        await Version(export, "a");
        inner.Throw = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(export, "a", Attach(1, 1)));
        inner.Throw = false;
        Assert.IsType<Rattach>(await Send(export, "a", Attach(2, 1)));
    }

    private static Task<object> Version(BoundedNamespaceExport export, string session, uint size = 512)
        => Send(export, session, NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, size, "9P2000")));

    private static Task<object> Send(BoundedNamespaceExport export, string session, NinePMessage message)
        => export.DispatchAsync(session, message, NinePDialect.NineP2000);

    private static NinePMessage Attach(ushort tag, uint fid)
        => NinePMessage.NewMsgTattach(new Tattach(tag, fid, NinePConstants.NoFid, "worker", "/"));

    private static NinePMessage Auth(ushort tag, uint afid)
        => NinePMessage.NewMsgTauth(new Tauth(tag, afid, "glenda", "/"));

    private static NinePMessage Walk(ushort tag, uint fid, uint newFid, params string[] names)
        => NinePMessage.NewMsgTwalk(new Twalk(tag, fid, newFid, names));

    private static string Error(object response) => Assert.IsType<Rerror>(response).Ename;

    private sealed class FakeDispatcher : INinePFSDispatcher, INinePSessionLifecycle
    {
        private readonly List<Type> forwarded = new();

        private Task? blocking;

        internal uint LastVersionSize { get; private set; }

        internal List<string> Closed { get; } = new();

        internal bool PartialWalks { get; set; }

        internal bool FailClunk { get; set; }

        internal bool AcceptAuth { get; set; }

        internal bool Throw { get; set; }

        internal uint? ReportedSize { get; set; }

        internal Task? Gate { get; set; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
        {
            if (Throw)
            {
                throw new InvalidOperationException("inner failure");
            }

            if (Gate is { } gate && message is not NinePMessage.MsgTflush)
            {
                Gate = null;
                blocking = gate;
                Entered.TrySetResult();
                await gate;
            }

            // Like flush(5), a flush waits for the request it flushes.
            if (message is NinePMessage.MsgTflush && blocking is { } pending)
            {
                await pending;
            }

            object request = message switch
            {
                NinePMessage.MsgTversion m => m.Item,
                NinePMessage.MsgTattach m => m.Item,
                NinePMessage.MsgTwalk m => m.Item,
                NinePMessage.MsgTclunk m => m.Item,
                NinePMessage.MsgTremove m => m.Item,
                NinePMessage.MsgTflush m => m.Item,
                NinePMessage.MsgTwrite m => m.Item,
                NinePMessage.MsgTauth m => m.Item,
                NinePMessage.MsgTopen m => m.Item,
                NinePMessage.MsgTcreate m => m.Item,
                NinePMessage.MsgTread m => m.Item,
                NinePMessage.MsgTstat m => m.Item,
                NinePMessage.MsgTwstat m => m.Item,
                _ => throw new NotSupportedException(),
            };
            lock (forwarded)
            {
                forwarded.Add(request.GetType());
            }

            return request switch
            {
                Tversion version => Version(version),
                Tattach attach => new Rattach(attach.Tag, new Qid(QidType.QTDIR, 0, 1)),
                Tauth auth when AcceptAuth => new Rauth(auth.Tag, new Qid(QidType.QTAUTH, 0, 1)),
                Twalk walk => new Rwalk(walk.Tag, Enumerable.Repeat(new Qid(QidType.QTDIR, 0, 1), PartialWalks ? walk.Wname.Length - 1 : walk.Wname.Length).ToArray()),
                Tclunk clunk => FailClunk ? new Rerror(clunk.Tag, "unknown fid") : new Rclunk(clunk.Tag),
                Tremove remove => new Rerror(remove.Tag, "permission denied"),
                Tflush flush => new Rflush(flush.Tag),
                Twrite write => new Rwrite(write.Tag, (uint)write.Data.Length),
                NinePSharp.Interfaces.ISerializable other => new Rerror(other.Tag, "denied"),
                _ => throw new NotSupportedException(),
            };
        }

        public Task CloseSessionAsync(string sessionId)
        {
            Closed.Add(sessionId);
            return Task.CompletedTask;
        }

        internal int Forwarded(Type type)
        {
            lock (forwarded)
            {
                return forwarded.Count(item => item == type);
            }
        }

        private Rversion Version(Tversion version)
        {
            LastVersionSize = version.MSize;
            return new Rversion(version.Tag, ReportedSize ?? version.MSize, "9P2000");
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
