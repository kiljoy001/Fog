using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Parser;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class ControlLimitsTests
{
    [Fact]
    public void EveryIndependentDispatcherAllowanceMustBePositive()
    {
        using var fixture = new ControlFixture();
        var valid = fixture.Limits;
        foreach (var invalid in new[]
        {
            valid with { Sessions = 0 }, valid with { FidsPerSession = 0 }, valid with { RequestsPerSession = 0 },
            valid with { MessageSize = 255 }, valid with { MessageSize = (uint)int.MaxValue + 1 },
            valid with { SnapshotBytesPerSession = 0 }, valid with { SnapshotLifetime = TimeSpan.Zero }, valid with { SessionLifetime = TimeSpan.Zero },
        }) Assert.Throws<ArgumentOutOfRangeException>(() => new FogNinePDispatcher(fixture.Tree, fixture.Policy, invalid));
        Assert.Throws<ArgumentNullException>(() => new FogNinePDispatcher(null!, fixture.Policy, valid));
        Assert.Throws<ArgumentNullException>(() => new FogNinePDispatcher(fixture.Tree, null!, valid));
        Assert.Throws<ArgumentNullException>(() => new FogNinePDispatcher(fixture.Tree, fixture.Policy, null!));
    }

    [Fact]
    public async Task VersionSessionsFidsAndRequestFramesHaveSeparateBounds()
    {
        using var fixture = new ControlFixture();
        var dispatcher = new FogNinePDispatcher(fixture.Tree, fixture.Policy, fixture.Limits with { Sessions = 1, FidsPerSession = 1, MessageSize = 256 });
        Task<object> Send(NinePMessage message, string session = "bounded") => dispatcher.DispatchAsync(session, message, NinePDialect.NineP2000, fixture.NodeCertificate);
        void Error(string code, object result) => Assert.Equal(code, Assert.IsType<Rerror>(result).Ename);
        Error("not-ready", await Send(NinePMessage.NewMsgTread(new Tread(1, 1, 0, 1))));
        Error("invalid-request", await Send(NinePMessage.NewMsgTversion(new Tversion(65535, 255, "9P2000"))));
        Assert.Equal("unknown", Assert.IsType<Rversion>(await Send(NinePMessage.NewMsgTversion(new Tversion(65535, 256, "bad")))).Version);
        Error("not-ready", await Send(NinePMessage.NewMsgTattach(new Tattach(2, 1, NinePConstants.NoFid, "worker", "runtime"))));
        Assert.Equal(256U, Assert.IsType<Rversion>(await Send(NinePMessage.NewMsgTversion(new Tversion(65535, 4096, "9P2000.L")))).MSize);
        Error("limit", await Send(NinePMessage.NewMsgTversion(new Tversion(65535, 256, "9P2000")), "second"));
        Assert.IsType<Rattach>(await Send(NinePMessage.NewMsgTattach(new Tattach(2, 1, NinePConstants.NoFid, "worker", "runtime"))));
        Error("busy", await Send(NinePMessage.NewMsgTattach(new Tattach(3, 1, NinePConstants.NoFid, "worker", "runtime"))));
        Error("limit", await Send(NinePMessage.NewMsgTwalk(new Twalk(4, 1, 2, []))));
        Error("invalid-request", await Send(NinePMessage.NewMsgTread(new Tread(65535, 1, 0, 1))));
        Error("invalid-request", await Send(NinePMessage.NewMsgTwrite(new Twrite(5, 1, 0, new byte[256]))));
        Error("denied", await Send(NinePMessage.NewMsgTremove(new Tremove(6, 1))));
        Error("invalid-request", await Send(NinePMessage.NewMsgTflush(new Tflush(7, 7))));
        Assert.IsType<Rflush>(await Send(NinePMessage.NewMsgTflush(new Tflush(8, 123))));
        Assert.IsType<Rflush>(await Send(NinePMessage.NewMsgTflush(new Tflush(9, 123))));
        await dispatcher.CloseSessionAsync("bounded");
        await dispatcher.CloseSessionAsync("bounded");
        Assert.IsType<Rversion>(await Send(NinePMessage.NewMsgTversion(new Tversion(65535, 256, "9P2000")), "second"));
        await dispatcher.CloseSessionAsync("second");
    }

    [Fact]
    public async Task SnapshotReservationIsReturnedOnClunkAndExpiresAtTheExactBoundary()
    {
        using var fixture = new ControlFixture(new(2, 16, 4, 4096, 86, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)));
        await fixture.Initialize();
        await fixture.Walk(1, 2, "control", "fixture", "clone");
        Assert.IsType<Ropen>(await fixture.Open(2, NinePConstants.OREAD));
        await fixture.Walk(1, 3, "control", "fixture", "clone");
        Assert.Equal("snapshot-limit", Assert.IsType<Rerror>(await fixture.Open(3, NinePConstants.OREAD)).Ename);
        Assert.Single(fixture.Store.LiveIds());
        Assert.IsType<Rclunk>(await fixture.Clunk(2));
        Assert.IsType<Ropen>(await fixture.Open(3, NinePConstants.OREAD));
        await fixture.Walk(1, 4, "control", "fixture", "clone");
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        // Expired read snapshot is freed; the transaction count is a separate limit.
        Assert.Equal("limit", Assert.IsType<Rerror>(await fixture.Open(4, NinePConstants.OREAD)).Ename);
        Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Read(3)).Ename);
        Assert.Equal("invalid-request", Assert.IsType<Rerror>(await fixture.Clunk(99)).Ename);
    }

    [Fact]
    public async Task FileAndDirectoryModesDoNotGrantUndeclaredOperations()
    {
        using var fixture = new ControlFixture();
        await fixture.Initialize();
        Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Open(1, NinePConstants.OWRITE)).Ename);
        Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Read(1)).Ename);
        Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Write(1, [1])).Ename);
        Assert.Equal("invalid-request", Assert.IsType<Rerror>(await fixture.Walk(1, 2, Enumerable.Repeat(".", 17).ToArray())).Ename);
        string id = await fixture.Clone();
        await fixture.Walk(1, 3, "control", "fixture", id, "request");
        Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Open(3, NinePConstants.OREAD)).Ename);
        await fixture.Walk(1, 4, "control", "fixture", id, "ctl");
        Assert.Equal("denied", Assert.IsType<Rerror>(await fixture.Open(4, NinePConstants.OREAD)).Ename);
        Assert.IsType<Ropen>(await fixture.Open(4, NinePConstants.OWRITE));
        foreach (string invalid in new[] { "commit", "commit\nrelease\n", "release\r\n", "" })
            Assert.Equal("invalid-request", Assert.IsType<Rerror>(await fixture.Write(4, System.Text.Encoding.UTF8.GetBytes(invalid))).Ename);
        await fixture.Walk(1, 5, "control", "fixture", id);
        Assert.IsType<Ropen>(await fixture.Open(5, NinePConstants.OREAD));
        var read = Assert.IsType<Rread>(await fixture.Read(5));
        int position = 0;
        var names = new List<string>();
        while (position < read.Data.Length) names.Add(new Stat(read.Data.Span, ref position).Name);
        Assert.Equal(new[] { "ctl", "extra", "payload", "reply", "request", "status" }, names.Order().ToArray());
        var stat = Assert.IsType<Rstat>(await fixture.Send(NinePMessage.NewMsgTstat(new Tstat(90, 5)))).Stat;
        Assert.Equal(0x80000140U, stat.Mode);
        Assert.Equal("fog", stat.Uid);
        Assert.Equal("fog", stat.Gid);
        Assert.Equal("fog", stat.Muid);
        Assert.Equal((ulong)read.Data.Length, stat.Length);
    }

    [Fact]
    public void ListenerRejectsInvalidLimitsBeforeOpeningASocket()
    {
        using var fixture = new ControlFixture();
        foreach (var limits in new[] { (0, 1, 1), (1, 0, 1), (1, 1, 0) })
            Assert.Throws<ArgumentException>(() => new FogNodeListener(new IPEndPoint(IPAddress.Loopback, 0), fixture.ServerCertificate,
                fixture.Policy, fixture.Dispatcher, NullLogger.Instance, limits.Item1, TimeSpan.FromSeconds(limits.Item2), TimeSpan.FromSeconds(limits.Item3)));
    }

    [Fact]
    public async Task ListenerLifecycleIsExplicitAndRepeatedDisposalIsSafe()
    {
        using var fixture = new ControlFixture();
        var listener = fixture.Listen();
        Assert.Throws<InvalidOperationException>(listener.Start);
        await listener.DisposeAsync();
        await listener.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(listener.Start);
    }
}
