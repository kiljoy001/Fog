using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using Xunit;
using Attr = NinePSharp.Fog.Commands.Attr;
using AttrType = NinePSharp.Fog.Commands.AttrType;
using Factotum = NinePSharp.Fog.Commands.Factotum;
using Fsstate = NinePSharp.Fog.Commands.Fsstate;
using Key = NinePSharp.Fog.Commands.Key;
using Keyinfo = NinePSharp.Fog.Commands.Keyinfo;
using Rpc = NinePSharp.Fog.Commands.Rpc;
using Srv = NinePSharp.Fog.Commands.Srv;

namespace NinePSharp.Fog.Rc.Tests;

// auth/factotum's file server, spoken to on the pipe it posts in /srv: what a client timing its
// messages sees, as the reads that wait for needkey, confirm and the log, and what a mount's own
// attach makes of the user.
public sealed class FactotumTests : IAsyncLifetime
{
    private const uint NoFid = uint.MaxValue;
    private readonly FakeHostNetwork host = new();
    private readonly FakeHostConsole console = new();
    private readonly ManualClock clock = new() { Now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero) };

    // The host's nvram, an Nvrsafe's worth.
    private readonly byte[] nvram = new byte[126];
    private FogKernel kernel = null!;
    private Process init = null!;
    private int fd = -1;
    private ushort tag;

    public async Task InitializeAsync()
    {
        var programs = new Dictionary<string, ProgramMain>(Commands.Commands.All)
        {
            // A secstore holding one key, which it prints as -G factotum asks, its arguments named in it.
            ["auth/secstore"] = (p, argv) => p.WriteAsync(1, Encoding.UTF8.GetBytes($"key proto=pass server={string.Join('.', argv)} user=s !password=kept\n")).AsTask(),
        };
        kernel = FogKernel.InMemory(programs, "glenda", clock, host, console, new HostNvram(nvram));
        init = await kernel.BootAsync();
    }

    public Task DisposeAsync()
    {
        foreach (Process process in kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheOwnerOpensByTheOwnersBitsAndAnyoneElseByTheOthers()
    {
        await StartAsync();
        await AttachAsync(1, "none");
        Assert.Equal("permission denied", Error(await OpenAsync(1, 10, "ctl", NinePConstants.OWRITE)));
        Assert.Equal("permission denied", Error(await OpenAsync(1, 11, "log", NinePConstants.OREAD)));
        Assert.Equal((byte)MessageTypes.Ropen, (await OpenAsync(1, 12, "rpc", NinePConstants.ORDWR))[4]);
        await AttachAsync(2, "glenda");
        Assert.Equal((byte)MessageTypes.Ropen, (await OpenAsync(2, 20, "ctl", NinePConstants.OWRITE))[4]);
    }

    [Fact]
    public async Task AnAttachIsOfTheRootOrOfFactotumAndOfNothingElse()
    {
        await StartAsync();
        Assert.Equal((QidType.QTDIR, 0ul), Qid(await RpcAsync(t => new Tattach(t, 1, NoFid, "glenda", string.Empty))));
        Assert.Equal((QidType.QTDIR, 1ul), Qid(await RpcAsync(t => new Tattach(t, 2, NoFid, "glenda", "factotum"))));
        long[] serving = Serving();
        Assert.Equal("unknown mount spec", Error(await RpcAsync(t => new Tattach(t, 3, NoFid, "glenda", "other"))));
        await Task.Delay(50);
        Assert.Equal(serving, Serving());
    }

    [Fact]
    public async Task AWalkReachesFactotumAndItsFilesAndTheirDotDots()
    {
        await StartAsync();
        Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, 1, NoFid, "glenda", string.Empty)))[4]);
        Qid[] walked = new Rwalk(await RpcAsync(t => new Twalk(t, 1, 2, ["..", "factotum", "ctl"]))).Wqid;
        Assert.Equal([(QidType.QTDIR, 0ul), (QidType.QTDIR, 1ul), (QidType.QTFILE, 7ul)], walked.Select(q => (q.Type, q.Path)));
        Qid[] back = new Rwalk(await RpcAsync(t => new Twalk(t, 1, 3, ["factotum", ".."]))).Wqid;
        Assert.Equal([(QidType.QTDIR, 1ul), (QidType.QTDIR, 0ul)], back.Select(q => (q.Type, q.Path)));
        string[] files = ["confirm", "needkey", "ctl", "rpc", "proto", "log"];
        ulong[] paths = [5, 8, 7, 2, 4, 6];
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            Assert.Equal(paths[i], Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 10, ["factotum", file]))).Wqid[1..]).Path);
            Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 10)))[4]);
        }

        Assert.Equal("not found", Error(await RpcAsync(t => new Twalk(t, 1, 4, ["nonesuch"]))));
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 5, ["factotum", "nonesuch"]))).Wqid);
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 7, ["factotum"])))[4]);
        Assert.Equal("not found", Error(await RpcAsync(t => new Twalk(t, 7, 8, ["nonesuch"]))));
        Assert.Equal("walk in non-directory", Error(await RpcAsync(t => new Twalk(t, 2, 6, ["x"]))));
    }

    [Fact]
    public async Task EachFileStatsWithItsModeAsTheOwnersAtTheTimeItIsAsked()
    {
        await StartAsync();
        Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, 1, NoFid, "none", string.Empty)))[4]);
        uint now = (uint)clock.Now.ToUnixTimeSeconds();
        Assert.Equal(("/", 0x80000000u | 0b101_101_101, "glenda", now), Stat(await RpcAsync(t => new Tstat(t, 1))));
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 2, ["factotum"])))[4]);
        Assert.Equal(("factotum", 0x80000000u | 0b101_101_101, "glenda", now), Stat(await RpcAsync(t => new Tstat(t, 2))));
        (string Name, uint Mode)[] files = [("confirm", 0x20000000u | 0b110_000_000), ("needkey", 0x20000000u | 0b110_000_000), ("ctl", 0b110_100_100), ("rpc", 0b110_110_110), ("proto", 0b100_100_100), ("log", 0x20000000u | 0b100_000_000)];
        foreach ((string name, uint mode) in files)
        {
            Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 2, 3, [name])))[4]);
            Assert.Equal((name, mode, "glenda", now), Stat(await RpcAsync(t => new Tstat(t, 3))));
            Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 3)))[4]);
        }
    }

    [Fact]
    public async Task TheDirectoriesListFactotumAndItsFilesInDirtabsOrder()
    {
        await StartAsync();
        Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, 1, NoFid, "glenda", string.Empty)))[4]);
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 2, [])))[4]);
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        Assert.Equal(["factotum"], await ListAsync(2));
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 4, [])))[4]);
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 4, NinePConstants.OREAD)))[4]);
        int at = 0;
        Assert.Equal(0x80000000u | 0b101_101_101, new NinePSharp.Messages.Stat(new Rread(await RpcAsync(t => new Tread(t, 4, 0, 8192))).Data.Span, ref at).Mode);
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 3, ["factotum"])))[4]);
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 3, NinePConstants.OREAD)))[4]);
        Assert.Equal(["confirm", "needkey", "ctl", "rpc", "proto", "log"], await ListAsync(3));
        Assert.Equal("is a directory", Error(await RpcAsync(t => new Topen(t, 1, NinePConstants.ORDWR))));
    }

    [Fact]
    public async Task AnOpenTakesOnlyTheModeAndOtruncAndAnExclusiveFileIsFreeOnceClunked()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        Assert.Equal("permission denied", Error(await OpenAsync(1, 10, "ctl", NinePConstants.OWRITE | NinePConstants.ORCLOSE)));
        Assert.Equal((byte)MessageTypes.Ropen, (await OpenAsync(1, 11, "ctl", NinePConstants.OWRITE | NinePConstants.OTRUNC))[4]);
        Assert.Equal("permission denied", Error(await OpenAsync(1, 12, "proto", NinePConstants.OEXEC)));
        Assert.Equal((byte)MessageTypes.Ropen, (await OpenAsync(1, 13, "needkey", NinePConstants.ORDWR))[4]);
        Assert.Equal("file in use", Error(await OpenAsync(1, 14, "needkey", NinePConstants.ORDWR)));
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 13)))[4]);
        Assert.Equal((byte)MessageTypes.Ropen, (await OpenAsync(1, 15, "needkey", NinePConstants.ORDWR))[4]);
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 14)))[4]);
        Assert.Equal("file in use", Error(await OpenAsync(1, 16, "needkey", NinePConstants.ORDWR)));
    }

    [Fact]
    public async Task ALineOfCtlOrProtoThatDoesNotFitTheReadIsRefused()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        await WriteAsync(await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE), "key proto=pass server=x user=u !password=p");
        uint ctl = await OpenFidAsync(1, 11, "ctl", NinePConstants.OREAD);
        await WriteAsync(10, "key proto=pass server=y user=u confirm=yes disabled=by.factotum !password=p");
        Assert.Equal("rpc too small", Error(await RpcAsync(t => new Tread(t, ctl, 0, 20))));
        Assert.Equal("rpc too small", Error(await RpcAsync(t => new Tread(t, ctl, 0, 42))));
        Assert.Equal("key proto=pass server=x user=u !password?\n", Text(await RpcAsync(t => new Tread(t, ctl, 0, 43))));
        Assert.Equal("key proto=pass server=y user=u confirm=yes disabled=by.factotum !password?\n", Text(await RpcAsync(t => new Tread(t, ctl, 0, 8192))));
        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Tread(t, ctl, 0, 46))));
        uint proto = await OpenFidAsync(1, 12, "proto", NinePConstants.OREAD);
        Assert.Equal("rpc too small", Error(await RpcAsync(t => new Tread(t, proto, 0, 5))));
        Assert.Equal("p9any\n", Text(await RpcAsync(t => new Tread(t, proto, 0, 6))));
        Assert.Equal("dp9ik\n", Text(await RpcAsync(t => new Tread(t, proto, 0, 6))));
        Assert.Equal("pass\n", Text(await RpcAsync(t => new Tread(t, proto, 0, 5))));
        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Tread(t, proto, 0, 5))));
    }

    [Fact]
    public async Task AnRpcIsShorterThan4096BytesAndAnswersAReadThatHasNoRoomWithWhatItWants()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        await WriteAsync(await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE), "key proto=pass server=x user=u !password=" + new string('p', 70));
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        Assert.Equal("rpc too large", Error(await RpcAsync(t => new Twrite(t, rpc, 0, new byte[4096]))));
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
        Assert.Equal("toosmall 72", await RpcCallAsync(rpc, "read", 64));
        Assert.Equal("toosmall 72", await RpcCallAsync(rpc, "read", 74));
        Assert.Equal("ok u " + new string('p', 70), await RpcCallAsync(rpc, "read", 75));
        Assert.Equal("ok u " + new string('p', 70), await RpcCallAsync(rpc, "read"));
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
    }

    [Fact]
    public void AnAttributeIsANameWithAValueAQueryOrABareNameWhoseValueIsEmpty()
    {
        Attr? a = Attr.Parse("=x b c? d= e=f=g");
        Assert.Equal([(AttrType.Nameval, string.Empty, "x"), (AttrType.Nameval, "b", string.Empty), (AttrType.Query, "c", string.Empty), (AttrType.Nameval, "d", string.Empty), (AttrType.Nameval, "e", "f=g")], a!.All.Select(x => (x.Type, x.Name, x.Val)));
    }

    [Fact]
    public void AQueryGoesWhereItsNameHasAValue()
    {
        Assert.Equal("y=1 x=2 z?", Attr.Format(Attr.Parse("x? y=1 x? x=2 x? z?")));
    }

    [Fact]
    public async Task AnRpcWaitsOnNeedkeyForTheKeyItLacks()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        uint needkey = await OpenFidAsync(1, 10, "needkey", NinePConstants.ORDWR);
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        await WriteAsync(rpc, "start proto=pass server=x role=client");
        ushort waiting = await SendAsync(t => new Tread(t, rpc, 0, 8192));
        Assert.Equal($"needkey tag={waiting} !password? proto=pass server=x user?\n", Text(await RpcAsync(t => new Tread(t, needkey, 0, 8192))));
        await WriteAsync(await OpenFidAsync(1, 12, "ctl", NinePConstants.OWRITE), "key proto=pass server=x user=u !password=p");
        Assert.Equal("empty write", Error(await RpcAsync(t => new Twrite(t, needkey, 0, Array.Empty<byte>()))));
        Assert.Equal("no tag", Error(await RpcAsync(t => new Twrite(t, needkey, 0, "answer=x"u8.ToArray()))));
        Assert.Equal("tag not found", Error(await RpcAsync(t => new Twrite(t, needkey, 0, "tag=999"u8.ToArray()))));
        long[] before = Serving();
        ushort written = await SendAsync(t => new Twrite(t, needkey, 0, Encoding.UTF8.GetBytes($"tag={waiting}")));
        Assert.Equal(["ok", string.Empty], await RepliesAsync(waiting, written));
        Assert.Equal("tag not found", Error(await RpcAsync(t => new Twrite(t, needkey, 0, Encoding.UTF8.GetBytes($"tag={waiting}")))));
        await WriteAsync(rpc, "start proto=pass server=y role=client");
        waiting = await SendAsync(t => new Tread(t, rpc, 0, 8192));
        Assert.StartsWith("needkey tag=", Text(await RpcAsync(t => new Tread(t, needkey, 0, 8192))), StringComparison.Ordinal);
        written = await SendAsync(t => new Twrite(t, needkey, 0, Encoding.UTF8.GetBytes($"tag={waiting} error='no key here'")));
        Assert.Equal(["error no key here", string.Empty], await RepliesAsync(waiting, written));
        Assert.Equal(before, Serving());
    }

    [Fact]
    public async Task AKeyThatAsksToBeConfirmedIsUsedOnlyOnceConfirmSaysYes()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        await WriteAsync(await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE), "key proto=pass server=x user=u confirm=yes !password=p");
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        Assert.Equal("confirm is closed", Error(await RpcStartAsync(rpc, "start proto=pass server=x role=client")));
        Assert.Equal("rpc already pending; read to clear", Error(await RpcAsync(t => new Twrite(t, rpc, 0, "start"u8.ToArray()))));
        uint confirm = await OpenFidAsync(1, 12, "confirm", NinePConstants.ORDWR);
        ushort waiting = await SendAsync(t => new Tread(t, rpc, 0, 8192));
        Assert.Equal("confirm tag=0 proto=pass server=x user=u confirm=yes\n", Text(await RpcAsync(t => new Tread(t, confirm, 0, 8192))));
        Assert.Equal("no tag", Error(await RpcAsync(t => new Twrite(t, confirm, 0, "answer=yes"u8.ToArray()))));
        Assert.Equal("no answer", Error(await RpcAsync(t => new Twrite(t, confirm, 0, "tag=0"u8.ToArray()))));
        Assert.Equal("bad answer", Error(await RpcAsync(t => new Twrite(t, confirm, 0, "tag=0 answer=maybe"u8.ToArray()))));
        long[] before = Serving();
        Assert.Equal("tag not found", Error(await RpcAsync(t => new Twrite(t, confirm, 0, "tag=7 answer=yes"u8.ToArray()))));
        Assert.Equal("empty write", Error(await RpcAsync(t => new Twrite(t, confirm, 0, Array.Empty<byte>()))));
        ushort written = await SendAsync(t => new Twrite(t, confirm, 0, "tag=0 answer=yes"u8.ToArray()));
        Assert.Equal(["ok", string.Empty], await RepliesAsync(waiting, written));
        Assert.Equal("tag not found", Error(await RpcAsync(t => new Twrite(t, confirm, 0, "tag=0 answer=yes"u8.ToArray()))));
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
        uint other = await OpenFidAsync(1, 13, "rpc", NinePConstants.ORDWR);
        await WriteAsync(other, "start proto=pass server=x role=client");
        waiting = await SendAsync(t => new Tread(t, other, 0, 8192));
        Assert.Equal("confirm tag=1 proto=pass server=x user=u confirm=yes\n", Text(await RpcAsync(t => new Tread(t, confirm, 0, 8192))));
        written = await SendAsync(t => new Twrite(t, confirm, 0, "tag=1 answer=no"u8.ToArray()));

        // A key found but refused leaves findkey nothing to ask for.
        Assert.Equal(["needkey ", string.Empty], await RepliesAsync(waiting, written));
        Assert.Equal(before, Serving());
    }

    [Fact]
    public async Task AFlushInterruptsAnRpcOrAReadThatWaits()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        uint needkey = await OpenFidAsync(1, 10, "needkey", NinePConstants.ORDWR);
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        await WriteAsync(rpc, "start proto=pass server=x role=client");
        ushort waiting = await SendAsync(t => new Tread(t, rpc, 0, 8192));
        Assert.StartsWith($"needkey tag={waiting} ", Text(await RpcAsync(t => new Tread(t, needkey, 0, 8192))), StringComparison.Ordinal);
        ushort flush = await SendAsync(t => new Tflush(t, waiting));
        Assert.Equal(["!interrupted", string.Empty], await RepliesAsync(waiting, flush));
        uint log = await OpenFidAsync(1, 12, "log", NinePConstants.OREAD);
        Assert.StartsWith("1: no key matches ", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))), StringComparison.Ordinal);
        foreach (uint fid in new[] { needkey, log })
        {
            waiting = await SendAsync(t => new Tread(t, fid, 0, 8192));
            flush = await SendAsync(t => new Tflush(t, waiting));
            Assert.Equal(["!interrupted", string.Empty], await RepliesAsync(waiting, flush));
        }

        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Tflush(t, 999))));
    }

    [Fact]
    public async Task TheLogKeepsWhatDebuggingRecordsAndCutsALineToFitTheRead()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        uint ctl = await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE);
        await WriteAsync(ctl, "debug");
        await WriteAsync(ctl, "key proto=pass server=x user=u !password=p");
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
        Assert.Equal("ok u p", await RpcCallAsync(rpc, "read"));
        Assert.StartsWith("phase", await RpcCallAsync(rpc, "write x"), StringComparison.Ordinal);
        Assert.Equal("needkey !password? proto=pass server=y user?", await RpcCallAsync(rpc, "start proto=pass server=y role=client"));
        uint log = await OpenFidAsync(1, 12, "log", NinePConstants.OREAD);
        Assert.Equal("factotum: read request count too short", Error(await RpcAsync(t => new Tread(t, log, 0, 4))));
        Assert.Equal("1: start proto=pass server=x role=client user=u yields phase 0: ok\n", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
        Assert.Equal("1: implicit close due to second start; old attr 'proto=pass server=x role=client user=u'\n", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
        Assert.Equal("2: start proto=pass server=x role=client user=u yields phase 0: ok\n", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
        Assert.Equal("2: read 3 in phase 0 yields phase 0: ok\n", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
        Assert.Equal("2: write 1 in phase 0 yields phase 0: phase: protocol phase error: write in state 0\n", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
        Assert.Equal("2: implicit close due to second start; old attr 'proto=pass server=x role=client user=u'\n", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
        Assert.Equal("3: no key matches proto=pass server=y role=client user? !password?  \n", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
        Assert.Equal("3: start proto=pass server=y role=client yields phase Notstarted: needkey !password? proto=pass server=y user?\n", Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
    }

    [Fact]
    public async Task VersionIsAnsweredOnlyAloneAndGivesTheSmallerMsizeOrUnknown()
    {
        await StartAsync();
        Assert.Equal("version: message size too small", Error(await RpcAsync(t => new Tversion(t, 255, "9P2000"))));
        var unknown = new Rversion(await RpcAsync(t => new Tversion(t, 8216, "XP2000")));
        Assert.Equal((256u, "unknown"), (unknown.MSize, unknown.Version));
        var big = new Rversion(await RpcAsync(t => new Tversion(t, 2u * 1024 * 1024, "9P2000.u")));
        Assert.Equal((1024u * 1024, "9P2000"), (big.MSize, big.Version));
        await AttachAsync(1, "glenda");
        uint needkey = await OpenFidAsync(1, 10, "needkey", NinePConstants.ORDWR);
        ushort waiting = await SendAsync(t => new Tread(t, needkey, 0, 8192));
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Tversion(t, 8216, "9P2000"))));
        ushort flush = await SendAsync(t => new Tflush(t, waiting));
        Assert.Equal(["!interrupted", string.Empty], await RepliesAsync(waiting, flush));
    }

    [Fact]
    public async Task AuthCreateRemoveAndWstatAreRefusedAsLib9pRefusesThemWithoutAHandler()
    {
        await StartAsync();
        Assert.Equal("auth/factotum: authentication not required", Error(await RpcAsync(t => new Tauth(t, 9, "glenda", string.Empty))));
        await AttachAsync(1, "glenda");
        Assert.Equal("create prohibited", Error(await RpcAsync(t => new Tcreate(t, 1, "x", 0b110_110_110, NinePConstants.OWRITE))));
        uint ctl = await OpenFidAsync(1, 10, "ctl", NinePConstants.OREAD);
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Tcreate(t, ctl, "x", 0b110_110_110, NinePConstants.OWRITE))));
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 11, ["ctl"])))[4]);
        Assert.Equal("create in non-directory", Error(await RpcAsync(t => new Tcreate(t, 11, "x", 0b110_110_110, NinePConstants.OWRITE))));
        Assert.Equal("wstat prohibited", Error(await RpcAsync(t => new Twstat(t, 11, Process.NullDir))));
        Assert.Equal("remove prohibited", Error(await RpcAsync(t => new Tremove(t, 11))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tremove(t, 11))));
    }

    [Fact]
    public async Task AFidMustBeKnownForEachRequestAndNewForThoseThatMakeOne()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        Assert.Equal("duplicate fid", Error(await RpcAsync(t => new Tattach(t, 1, NoFid, "glenda", "factotum"))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tattach(t, 2, 77, "glenda", "factotum"))));
        Assert.Equal("duplicate fid", Error(await RpcAsync(t => new Tauth(t, 1, "glenda", string.Empty))));
        Assert.Empty(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 2, []))).Wqid);
        Assert.Equal("duplicate fid", Error(await RpcAsync(t => new Twalk(t, 1, 2, []))));
        foreach (Func<ushort, ISerializable> request in new Func<ushort, ISerializable>[]
        {
            t => new Twalk(t, 77, 78, []),
            t => new Topen(t, 77, NinePConstants.OREAD),
            t => new Tcreate(t, 77, "x", 0, NinePConstants.OREAD),
            t => new Tread(t, 77, 0, 10),
            t => new Twrite(t, 77, 0, "x"u8.ToArray()),
            t => new Tclunk(t, 77),
            t => new Tstat(t, 77),
            t => new Twstat(t, 77, Process.NullDir),
        })
        {
            Assert.Equal("unknown fid", Error(await RpcAsync(request)));
        }
    }

    [Fact]
    public async Task AWalkClonesFollowsAsFarAsItCanAndRefusesWhatLib9pDoesNot()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        Assert.Empty(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 2, []))).Wqid);
        Assert.Equal((QidType.QTDIR, 1ul), Stat2(await RpcAsync(t => new Tstat(t, 2))));
        Assert.Equal("lib9p: unused documented feature not implemented", Error(await RpcAsync(t => new Twalk(t, 2, 2, ["ctl", "x"]))));
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 2, 2, ["ctl"]))).Wqid);
        Assert.Equal((QidType.QTFILE, 7ul), Stat2(await RpcAsync(t => new Tstat(t, 2))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tstat(t, 3))));
        uint ctl = await OpenFidAsync(1, 4, "ctl", NinePConstants.OREAD);
        Assert.Equal("cannot clone open fid", Error(await RpcAsync(t => new Twalk(t, ctl, 5, []))));
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Topen(t, ctl, NinePConstants.OREAD))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tstat(t, 5))));
    }

    [Fact]
    public async Task AReadOrWriteMustSuitHowItsFidWasOpened()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 2, ["ctl"])))[4]);
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Tread(t, 2, 0, 10))));
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Twrite(t, 2, 0, "x"u8.ToArray()))));
        uint ctl = await OpenFidAsync(1, 3, "ctl", NinePConstants.OWRITE);
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Tread(t, ctl, 0, 10))));
        uint proto = await OpenFidAsync(1, 4, "proto", NinePConstants.OREAD);
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Twrite(t, proto, 0, "x"u8.ToArray()))));
        Assert.Equal("bad offset", Error(await RpcAsync(t => new Tread(t, proto, 1ul << 63, 10))));
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Tread(t, proto, 0, 1u << 31))));
        uint rpc = await OpenFidAsync(1, 5, "rpc", NinePConstants.ORDWR);
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Twrite(t, rpc, 1ul << 63, "x"u8.ToArray()))));
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 6, [])))[4]);
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 6, NinePConstants.OREAD)))[4]);
        Assert.Equal("bad offset", Error(await RpcAsync(t => new Tread(t, 6, 5, 8192))));
        byte[] first = new Rread(await RpcAsync(t => new Tread(t, 6, 0, 8192))).Data.ToArray();
        Assert.NotEmpty(first);
        Assert.Empty(new Rread(await RpcAsync(t => new Tread(t, 6, (ulong)first.Length, 8192))).Data.ToArray());
    }

    [Fact]
    public async Task ATagInUseIsADuplicate()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        uint needkey = await OpenFidAsync(1, 10, "needkey", NinePConstants.ORDWR);
        ushort waiting = await SendAsync(t => new Tread(t, needkey, 0, 8192));
        await init.WriteAsync(fd, Encode(new Tstat(waiting, 1)));
        Assert.Equal("!duplicate tag", (await RepliesAsync(waiting))[0]);
    }

    [Fact]
    public async Task ItMountsOnMntFactotumUnlessGivenAMountPointAndMakesItsServiceFileWritableByAll()
    {
        await StartAsync();
        Stat posted = await init.StatAsync("/srv/factotum.test");
        Assert.Equal(0b110_110_110u, posted.Mode & 0b111_111_111);

        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Assert.Equal(["confirm", "needkey", "ctl", "rpc", "proto", "log"], await NamesAsync("/mnt/factotum"));
        await init.CreateAsync("/tmp/m", NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101);
        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-m", "/tmp/m"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Assert.Equal(["factotum"], await NamesAsync("/tmp/m"));
    }

    [Fact]
    public async Task WithoutProcItWarnsItCannotProtectItselfFromDebuggingUnlessPOrDSaysNotTo()
    {
        await init.CloseAsync(await init.CreateAsync("/tmp/noproc", NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        (int errors, int written) = await PipeAsync();
        Assert.Equal(string.Empty, await RunBoundAsync("/tmp/noproc", "/proc", written, "-n", "-m", "/n"));
        Assert.StartsWith("Warning! auth/factotum can't protect itself from debugging: ", await ReadErrorsAsync(errors), StringComparison.Ordinal);
        foreach (string flag in (string[])["-p", "-d"])
        {
            Assert.Equal(string.Empty, await RunBoundAsync("/tmp/noproc", "/proc", written, flag, "-n", "-m", "/n"));
            await init.WriteAsync(written, "end"u8.ToArray());
            Assert.Equal("end", await ReadErrorsAsync(errors));
        }
    }

    [Fact]
    public async Task ItWarnsWhenItsServiceCannotBeMadeWritableByAll()
    {
        await init.CloseAsync(await init.CreateAsync("/tmp/srv", NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        (int errors, int written) = await PipeAsync();
        Assert.Equal(string.Empty, await RunBoundAsync("/tmp/srv", "/srv", written, "-n", "-p", "-m", "/n", "-s", "f"));
        Assert.StartsWith("factotum warning: cannot chmod 666 /srv/f: ", await ReadErrorsAsync(errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutNItAddsTheKeysSecstoreHoldsUnlessItIsAServersWhichAsksForNone()
    {
        // Mounted on /mnt, its ctl takes what secstore -G factotum prints.
        Assert.Equal(string.Empty, await RunAsync(-1, "-p"));
        Assert.Equal("key proto=pass server=secstore.-G.factotum user=s !password?\n", await CtlLineAsync(1));
        Assert.StartsWith("needkey ", await StartRpcAsync("start proto=pass role=client server=q user?"), StringComparison.Ordinal);

        // -S's factotum keeps no secstore and asks for no key it lacks.
        Assert.Equal(string.Empty, await RunAsync(-1, "-S", "-p"));
        Assert.Equal(string.Empty, await CtlLineAsync(1));
        Assert.StartsWith("error ", await StartRpcAsync("start proto=pass role=client server=q user?"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DStartsItDebugging()
    {
        int errors = await StartWithErrorsAsync("-d");
        await AttachAsync(1, "glenda");
        uint ctl = await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE);
        await WriteAsync(ctl, "key proto=pass server=x user=u !password=p");
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
        Assert.Equal("1: start proto=pass server=x role=client user=u yields phase 0: ok\n", await ReadErrorsAsync(errors));
    }

    [Fact]
    public async Task AnArgumentItDoesNotTakeIsAUsageError()
    {
        foreach (string[] args in new[] { new[] { "-x" }, ["-n", "extra"], ["-g"], ["-m"] })
        {
            int[] pipe = (await init.PipeAsync()) is var (a, b) ? [a, b] : [];
            init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
            {
                await child.DupAsync(pipe[1], 2);
                await child.ExecAsync("/bin/auth/factotum", ["auth/factotum", .. args]);
            });
            Assert.Equal("usage", (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message.Split(": ")[^1]);
            await init.CloseAsync(pipe[1]);
            Assert.Equal("usage: auth/factotum [-DSdknpu] [-a authaddr] [-m mtpt] [-s service]\nor    auth/factotum -g 'params'\n", Encoding.UTF8.GetString((await init.ReadAsync(pipe[0], 8192)).Span));
            await init.CloseAsync(pipe[0]);
        }
    }

    [Fact]
    public async Task TheServerGoesOnInTheProcessAnRpcLeavesAndInTheOneItForkedHoweverTheRpcEnds()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        await WriteAsync(await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE), "key proto=pass server=x user=u !password=p");
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        Assert.Equal("error no current protocol", await RpcCallAsync(rpc, "read"));
        long[] before = Serving();
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
        Assert.Equal("!rpc read too small", await RpcCallAsync(rpc, "authinfo", 63));
        Assert.Equal("error authentication unfinished", Text(await RpcAsync(t => new Tread(t, rpc, 0, 8192))));
        Assert.Equal("!no rpc pending", Text(await RpcAsync(t => new Tread(t, rpc, 0, 8192))));
        Assert.Equal("ok u p", await RpcCallAsync(rpc, "read\0 x"));
        Assert.Equal("needkey !password? proto=pass server=y user?", await RpcCallAsync(rpc, "start proto=pass server=y role=client"));
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 10)))[4]);
        Assert.Equal(before, Serving());
    }

    [Fact]
    public async Task AFlushInterruptsAnRpcWaitingForConfirmAndLeavesTheOtherReadsOfTheLogWaiting()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        uint ctl = await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE);
        await WriteAsync(ctl, "key proto=pass server=x user=u confirm=yes !password=p");
        uint confirm = await OpenFidAsync(1, 11, "confirm", NinePConstants.ORDWR);
        uint rpc = await OpenFidAsync(1, 12, "rpc", NinePConstants.ORDWR);
        await WriteAsync(rpc, "start proto=pass server=x role=client");
        ushort waiting = await SendAsync(t => new Tread(t, rpc, 0, 8192));
        Assert.StartsWith("confirm tag=0 ", Text(await RpcAsync(t => new Tread(t, confirm, 0, 8192))), StringComparison.Ordinal);
        ushort flush = await SendAsync(t => new Tflush(t, waiting));
        Assert.Equal(["!interrupted", string.Empty], await RepliesAsync(waiting, flush));

        await WriteAsync(ctl, "debug");
        uint log = await OpenFidAsync(1, 13, "log", NinePConstants.OREAD);
        ushort first = await SendAsync(t => new Tread(t, log, 0, 8192)), second = await SendAsync(t => new Tread(t, log, 0, 8192));
        flush = await SendAsync(t => new Tflush(t, first));
        Assert.Equal(["!interrupted", string.Empty], await RepliesAsync(first, flush));
        await WriteAsync(ctl, "key proto=pass server=y user=u !password=p");

        // The flushed rpc is still pending, as fs.c leaves it.
        Assert.Equal("rpc already pending; read to clear", Error(await RpcAsync(t => new Twrite(t, rpc, 0, "start"u8.ToArray()))));
        rpc = await OpenFidAsync(1, 14, "rpc", NinePConstants.ORDWR);
        await WriteAsync(rpc, "start proto=pass server=y role=client");
        ushort read = await SendAsync(t => new Tread(t, rpc, 0, 8192));
        Assert.Equal(["ok", "2: start proto=pass server=y role=client user=u yields phase 0: ok\n"], await RepliesAsync(read, second));
    }

    [Fact]
    public async Task ALogLineCutToFitEndsBeforeTheRuneTheCutFallsInAndAReadOfFiveHoldsOnlyTheDots()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        uint ctl = await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE);
        await WriteAsync(ctl, "debug");
        await WriteAsync(ctl, "key proto=pass server=ü user=u !password=p");
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=ü role=client"));
        }

        uint log = await OpenFidAsync(1, 12, "log", NinePConstants.OREAD);
        string head = "1: start proto=pass server=";
        uint cut = (uint)Encoding.UTF8.GetByteCount(head) + 7;
        Assert.Equal(head + "...\n", Text(await RpcAsync(t => new Tread(t, log, 0, cut))));
        Assert.Equal("...\n", Text(await RpcAsync(t => new Tread(t, log, 0, 5))));
        string line = "2: start proto=pass server=ü role=client user=u yields phase 0: ok";
        uint fits = (uint)Encoding.UTF8.GetByteCount(line) + 2;
        Assert.Equal(line + "\n", Text(await RpcAsync(t => new Tread(t, log, 0, fits))));
        line = "2: implicit close due to second start; old attr 'proto=pass server=ü role=client user=u'";
        fits = (uint)Encoding.UTF8.GetByteCount(line) + 2;
        Assert.Equal(line[..^5] + "...\n", Text(await RpcAsync(t => new Tread(t, log, 0, fits - 1))));
    }

    [Fact]
    public async Task DebuggingPrintsEachLineLoggedOnceOnTheErrorOutput()
    {
        int errors = await StartWithErrorsAsync();
        await AttachAsync(1, "glenda");
        uint ctl = await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE);
        await WriteAsync(ctl, "debug");
        await WriteAsync(ctl, "key proto=pass server=x user=u !password=p");
        uint rpc = await OpenFidAsync(1, 11, "rpc", NinePConstants.ORDWR);
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
        Assert.Equal("ok u p", await RpcCallAsync(rpc, "read"));
        string expected = "1: start proto=pass server=x role=client user=u yields phase 0: ok\n1: read 3 in phase 0 yields phase 0: ok\n";
        var text = new StringBuilder();
        while (text.Length < expected.Length)
        {
            text.Append(Encoding.UTF8.GetString((await init.ReadAsync(errors, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span));
        }

        Assert.Equal(expected, text.ToString());
    }

    [Fact]
    public async Task TheLogSaysHowAStartFailedWaitedOrWantedMoreRoom()
    {
        await StartAsync();
        await AttachAsync(1, "glenda");
        uint ctl = await OpenFidAsync(1, 10, "ctl", NinePConstants.OWRITE);
        await WriteAsync(ctl, "debug");
        await WriteAsync(ctl, "key proto=pass server=x user=u !password=" + new string('p', 70));
        await WriteAsync(ctl, "key proto=pass server=c user=u confirm=yes !password=p");
        uint confirm = await OpenFidAsync(1, 11, "confirm", NinePConstants.ORDWR);
        uint rpc = await OpenFidAsync(1, 12, "rpc", NinePConstants.ORDWR);
        Assert.Equal("ok", await RpcCallAsync(rpc, "start proto=pass server=x role=client"));
        Assert.Equal("toosmall 72", await RpcCallAsync(rpc, "read", 64));
        await WriteAsync(rpc, "start proto=pass server=c role=client");
        ushort waiting = await SendAsync(t => new Tread(t, rpc, 0, 8192));
        Assert.StartsWith("confirm tag=0 ", Text(await RpcAsync(t => new Tread(t, confirm, 0, 8192))), StringComparison.Ordinal);
        ushort flush = await SendAsync(t => new Tflush(t, waiting));
        Assert.Equal(["!interrupted", string.Empty], await RepliesAsync(waiting, flush));
        await AttachAsync(2, "none");
        uint other = await OpenFidAsync(2, 20, "rpc", NinePConstants.ORDWR);
        Assert.StartsWith("error no key matches ", await RpcCallAsync(other, "start proto=pass server=x role=client"), StringComparison.Ordinal);

        uint log = await OpenFidAsync(1, 13, "log", NinePConstants.OREAD);
        var lines = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            lines.Add(Text(await RpcAsync(t => new Tread(t, log, 0, 8192))));
        }

        Assert.Equal(
            [
                "1: start proto=pass server=x role=client user=u yields phase 0: ok\n",
                "1: read 0 in phase 0 yields phase 0: toosmall 72\n",
                "1: implicit close due to second start; old attr 'proto=pass server=x role=client user=u'\n",
                "2: start proto=pass server=c role=client yields phase Notstarted: waiting for confirm\n",
                "3: no key matches proto=pass server=x role=client user? !password? owner=none owner='*'\n",
                "3: failure no key matches proto=pass server=x role=client user? !password?\n",
                "3: start proto=pass server=x role=client yields phase Notstarted: failure no key matches proto=pass server=x role=client user? !password?\n",
            ],
            lines);
    }

    [Fact]
    public async Task AKeyAskedForOnTheConsoleIsAnnouncedWithItsPublicAttributesAndItsQueriesPromptedFor()
    {
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            for (int i = 0; i < 3; i++)
            {
                await child.DupAsync(await child.OpenAsync("/dev/null", NinePConstants.ORDWR), i);
            }

            await child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n"]);
        });
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-g", "proto=pass server='a b' !password=p"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Assert.Equal("\n!Adding key: proto=pass server='a b'\n!\n", console.Text);
        int ctl = await init.OpenAsync("/mnt/factotum/ctl", NinePConstants.OREAD);
        Assert.Equal("key proto=pass server='a b' !password?\n", Encoding.UTF8.GetString((await init.ReadAsync(ctl, 8192)).Span));

        // Each query is asked for in turn, the secret ones last and without echo, and an empty
        // reply to user takes the user's own name.
        console.Type("\nsecret\n");
        Assert.Equal(string.Empty, await GetAsync("proto=pass server=x user? !password?"));
        Assert.EndsWith("\n!Adding key: proto=pass server=x\nuser[glenda]: password: \n!\n", console.Text, StringComparison.Ordinal);
        Assert.False(console.Raw);
        Assert.Equal("key proto=pass server=x user=glenda !password?\n", await CtlLineAsync(2));
        Assert.Equal(["secret"], await PasswordsAsync("server=x"));

        // Input that ends before a reply is the user giving up, which factotum says as it exits.
        (int errors, int written) = await PipeAsync();
        Assert.EndsWith("user terminated key input", await RunAsync(written, "-g", "proto=pass server=y dom?"), StringComparison.Ordinal);
        Assert.Equal("auth/factotum: user terminated key input\n", await ReadErrorsAsync(errors));
        Assert.EndsWith("\n!Adding key: proto=pass server=y\ndom: ", console.Text, StringComparison.Ordinal);
        console.Type("x\x7f");
        Assert.EndsWith("user terminated key input", await GetAsync("proto=pass server=y !password?"), StringComparison.Ordinal);
        Assert.EndsWith("password: \n", console.Text, StringComparison.Ordinal);
        Assert.False(console.Raw);

        Assert.EndsWith(": key without protos", await GetAsync(string.Empty), StringComparison.Ordinal);
        Assert.EndsWith("\n!Adding key:\n!\n", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyOnTheConsoleIsEditedAsItIsTyped()
    {
        await StartAsync();

        // Cooked, the console's own line is edited before readcons sees it, which ends it at a
        // return and leaves the rest of the line for the next question.
        console.Type("ab\bc\rhi\n");
        Assert.Equal(string.Empty, await GetAsync("proto=pass server=e user? dom?"));
        Assert.EndsWith("user[glenda]: dom: !\n", console.Text, StringComparison.Ordinal);
        Assert.Equal("key proto=pass server=e user=ac dom=hi \n", await CtlLineAsync(1));

        // Raw, readcons edits the reply itself: backspace takes back a rune, ^U the whole and asks
        // again, a control character is no part of it, and a return ends it as a newline does.
        console.Type("ab\u00e9\b\bc\x01\r" + "zz\x15hi\n");
        Assert.Equal(string.Empty, await GetAsync("proto=pass server=f user=u !password? !other?"));
        Assert.EndsWith("password: \nother: \nother: \n!\n", console.Text, StringComparison.Ordinal);
        Assert.Equal(["ac"], await PasswordsAsync("server=f"));
    }

    [Fact]
    public async Task STakesTheHostOwnersDp9ikKeyFromTheNvramAndNamesTheHostOwnerFromIt()
    {
        var safe = new NinePSharp.Fog.Commands.Nvrsafe();
        NinePSharp.Fog.Commands.Authsrv.Safecpy(safe.Authid, "bootes@fog");
        NinePSharp.Fog.Commands.Authsrv.Safecpy(safe.Authdom, "fog");
        Dp9ik.AuthKey key = Dp9ik.AuthKey.FromPassword("bootespw");
        key.AesKey.CopyTo(safe.Aesmachkey, 0);
        safe.Aesmachkey[0] = 0;
        key.DesKey.CopyTo(safe.Machkey, 0);
        Sum(safe);
        safe.ToBytes().CopyTo(nvram, 0);
        await StartAsync("-S");
        Assert.Equal("bootes", await ReadTextAsync("/dev/hostowner"));
        Assert.Equal("key proto=dp9ik user=bootes@fog dom=fog !hex? !password?\n", await CtlLineAsync(1));
        Assert.Equal(string.Empty, await CtlLineAsync(2));
    }

    [Fact]
    public async Task SWithAnNvramOfNoAesKeyAddsNoKeyAndWithABadOneAsksOnTheConsole()
    {
        var safe = new NinePSharp.Fog.Commands.Nvrsafe();
        NinePSharp.Fog.Commands.Authsrv.Safecpy(safe.Authid, "bootes");
        NinePSharp.Fog.Commands.Authsrv.Safecpy(safe.Authdom, "fog");
        Sum(safe);
        safe.ToBytes().CopyTo(nvram, 0);
        await StartAsync("-S");
        Assert.Equal("bootes", await ReadTextAsync("/dev/hostowner"));
        Assert.Equal(string.Empty, await CtlLineAsync(1));

        // A bad nvram, and no answer to the first question.
        nvram.AsSpan().Clear();
        (int errors, int written) = await PipeAsync();
        Assert.Equal(string.Empty, await RunAsync(written, "-S", "-n", "-p", "-m", "/n"));
        Assert.Equal("bad nvram des key\nbad authentication id\nbad authentication domain\nfactotum warning: cannot read nvram: input aborted\n", await ReadErrorsAsync(errors, 4));

        // -k asks however good the nvram is, and says nothing once it has the key.
        safe.ToBytes().CopyTo(nvram, 0);
        console.Type("glenda\nfog\n\npw\npw\n\n");
        Assert.Equal(string.Empty, await RunAsync(written, "-S", "-k", "-n", "-p", "-m", "/n"));
        await init.WriteAsync(written, "end"u8.ToArray());
        Assert.Equal("end", await ReadErrorsAsync(errors));
        Assert.Equal("glenda", NinePSharp.Fog.Commands.Authsrv.Str(NinePSharp.Fog.Commands.Nvrsafe.FromBytes(nvram).Authid));
        Assert.Equal("glenda", await ReadTextAsync("/dev/hostowner"));
    }

    [Fact]
    public async Task UAsksForTheHostOwnerWhenUserIsNotSetAndNamesItBeforeAnAt()
    {
        await init.RemoveAsync("/env/user").AsTask().ContinueWith(_ => { });
        console.Type("bootes@fog\n");
        await StartAsync("-u");
        Assert.EndsWith("user[glenda]: ", console.Text, StringComparison.Ordinal);
        Assert.Equal("bootes", await ReadTextAsync("/dev/hostowner"));
        Assert.Equal("bootes", init.User);

        // The files are the new host owner's, the owner factotum now runs as.
        await AttachAsync(1, "bootes");
        Assert.Equal((byte)MessageTypes.Ropen, (await OpenAsync(1, 10, "ctl", NinePConstants.OWRITE))[4]);
    }

    [Fact]
    public async Task UTakesUserFromTheEnvironmentAndSaysWhenTheHostOwnerCannotBeIt()
    {
        await WriteFileAsync("/env/user", "bootes");
        await StartAsync("-u");
        Assert.Equal("bootes", await ReadTextAsync("/dev/hostowner"));

        await WriteFileAsync("/env/user", new string('n', 28));
        (int errors, int written) = await PipeAsync();
        Assert.Equal(string.Empty, await RunAsync(written, "-u", "-n", "-p", "-m", "/n"));
        Assert.Equal($"factotum: setting /dev/hostowner to {new string('n', 28)}: name too long\n", await ReadErrorsAsync(errors));
        Assert.Equal("bootes", await ReadTextAsync("/dev/hostowner"));
    }

    [Fact]
    public async Task UWithNoUserAndNoReplyLeavesTheHostOwnerAndOneThatIsNotItCannotWriteIt()
    {
        await init.RemoveAsync("/env/user").AsTask().ContinueWith(_ => { });
        await StartAsync("-u");
        Assert.Equal("glenda", await ReadTextAsync("/dev/hostowner"));

        await WriteFileAsync("/env/user", "bootes");
        await WriteFileAsync("/dev/user", "none");
        Assert.Equal(string.Empty, await RunAsync(-1, "-u", "-n", "-p", "-m", "/n"));
        Assert.Equal("glenda", await ReadTextAsync("/dev/hostowner"));
    }

    [Fact]
    public void ANameValuePatternWantsItsValueInEitherListAndRoleAndDisabledOnlyWhereTheyAreGiven()
    {
        Assert.False(Factotum.Matchattr(Attr.Parse("a=1"), null, null));
        Assert.False(Factotum.Matchattr(Attr.Parse("u=1"), Attr.Parse("u=2"), null));
        Assert.False(Factotum.Matchattr(Attr.Parse("u=1"), Attr.Parse("v=1"), null));
        Assert.True(Factotum.Matchattr(Attr.Parse("u=1"), Attr.Parse("u=1"), null));
        Assert.True(Factotum.Matchattr(Attr.Parse("u=1"), null, Attr.Parse("u=1")));
        Assert.True(Factotum.Matchattr(Attr.Parse("role=client disabled=x"), Attr.Parse("proto=pass"), null));
        Assert.False(Factotum.Matchattr(Attr.Parse("role=client"), Attr.Parse("role=server"), null));
    }

    [Fact]
    public void SetattrsGivesTheFirstOfANameItsValueDropsTheRestAndAddsWhatIsNew()
    {
        Assert.Equal("x=1", Attr.Format(Factotum.Setattrs(Attr.Parse("x=1"), Attr.Parse("x?"))));
        Assert.Equal("y=0 x=1", Attr.Format(Factotum.Setattrs(Attr.Parse("y=0 x=1"), Attr.Parse("x?"))));
        Assert.Equal("y=1 x?", Attr.Format(Factotum.Setattrs(Attr.Parse("y=1"), Attr.Parse("x?"))));
        Assert.Equal("x=4 y=2", Attr.Format(Factotum.Setattrs(Attr.Parse("x=1 y=2 x=3"), Attr.Parse("x=4"))));
        Assert.Equal("x=1", Attr.Format(Factotum.Setattrs(null, Attr.Parse("x=1"))));
    }

    [Fact]
    public void SortattrSortsByNameAndLeavesEqualNamesAsItsMergeDoes()
    {
        Assert.Equal("a=2 b=3 c=1", Attr.Format(Factotum.Sortattr(Attr.Parse("c=1 a=2 b=3"))));
        Assert.Equal("x=3 x=1 x=2", Attr.Format(Factotum.Sortattr(Attr.Parse("x=1 x=2 x=3"))));
        Assert.Equal(string.Empty, Factotum.Attrnamefmt(null));
        Assert.Equal("a? b?", Factotum.Attrnamefmt(Attr.Parse("a=1 b?")));
    }

    [Fact]
    public void IsclientIsOneForAClientNoneForAServerAndAnErrorOtherwise()
    {
        Srv.Enter(init);
        Assert.Equal(string.Empty, Factotum.Errstr);
        Assert.Equal((1, 0), (Factotum.Isclient("client"), Factotum.Isclient("server")));
        Assert.Equal((-1, "role not specified"), (Factotum.Isclient(null), Factotum.Errstr));
        Assert.Equal((-1, "unknown role 'a b'"), (Factotum.Isclient("a b"), Factotum.Errstr));
    }

    [Fact]
    public void AFailureKeepsItsErrorOrTheErrstrAndAPhaseErrorNamesThePhase()
    {
        Factotum f = NewFactotum();
        var fss = new Fsstate();
        Assert.Equal((Rpc.Failure, string.Empty), (f.Failure(fss, null), fss.Err));
        Assert.Equal((Rpc.Failure, "bad", "bad"), (f.Failure(fss, "bad"), fss.Err, Factotum.Errstr));
        (int Phase, int Maxphase, string[] Names, string Named)[] phases =
        [
            (Fsstate.Broken, 0, [], "Broken"), (Fsstate.Established, 0, [], "Established"), (Fsstate.Notstarted, 0, [], "Notstarted"),
            (0, 1, ["HavePass"], "HavePass"), (1, 1, ["a", "b"], "1"), (1, 2, ["a"], "1"), (-4, 1, ["a"], "-4"),
        ];
        foreach ((int phase, int maxphase, string[] names, string named) in phases)
        {
            var s = new Fsstate { Phase = phase, Maxphase = maxphase, Phasename = names };
            Assert.Equal((Rpc.Phase, $"protocol phase error: read in state {named}"), (f.Phaseerror(s, "read"), Factotum.Errstr));
        }
    }

    [Fact]
    public void FindkeyGivesTheOwnerEveryKeyAndAnyoneElseTheirsAndThoseForAnyoneButNotTheDisabled()
    {
        Factotum f = NewFactotum();
        foreach (string key in new[] { "server=a", "server=b owner=none", "server=c owner=*", "server=d disabled=by.factotum" })
        {
            Assert.Equal(0, f.Ctlwrite($"key proto=pass {key} user=u !password=p", false));
        }

        Assert.Equal(["a", "b", "c"], Ring(f, string.Empty, false));
        Assert.Equal(["a", "b", "c", "d"], Ring(f, string.Empty, true));
        Assert.Equal(["b", "c"], Ring(f, "none", false));
        Assert.Equal(["c"], Ring(f, "x", false));
    }

    [Fact]
    public void FindkeyAsksForAKeyOnlyForTheOwnerAndOnlyWhenThereWereQueries()
    {
        Factotum f = NewFactotum();
        var fss = new Fsstate { Attr = Attr.Parse("server=z user?") };
        Assert.Equal(Rpc.Needkey, f.Findkey(out _, new Keyinfo(fss, null), null));
        Assert.Equal("server=z user?", fss.Keyinfo);
        Assert.Equal(Rpc.Failure, f.Findkey(out _, new Keyinfo(fss, null) { User = "x" }, null));
        Assert.Equal("no key matches server=z user? ", fss.Err);
        Assert.Equal(Rpc.Failure, f.Findkey(out _, new Keyinfo(fss, Attr.Parse("server=z")), null));
        Assert.Equal(Rpc.Failure, f.Findkey(out _, new Keyinfo(fss, Attr.Parse("proto=nope")), "proto=pass"));
        Assert.Equal("unknown protocol nope", fss.Err);
        Assert.Equal(Rpc.Failure, f.Findkey(out _, new Keyinfo(new Fsstate(), null), "proto=nope"));
        Assert.Equal("unknown protocol nope", Factotum.Errstr);
    }

    [Fact]
    public void AKeyReplacesOneWithTheSamePublicAttributesInItsPlaceAndIsOtherwiseAddedAtTheEndOrAtTheStart()
    {
        Factotum f = NewFactotum();
        Assert.Equal(0, f.Ctlwrite("key proto=pass server=a user=u !password=p", false));
        Assert.Equal(0, f.Ctlwrite("key proto=pass server=b user=u !password=p", false));
        Assert.Equal(0, f.Ctlwrite("key proto=pass server=a user=u !password=q", false));
        Assert.Equal(0, f.Ctlwrite("key proto=pass server=b user=u !password=r", false));
        Assert.Equal(0, f.Ctlwrite("key proto=pass server=a user=u dom=d !password=s", false));
        Assert.Equal(0, f.Ctlwrite("key proto=pass server=z user=u !password=t", true));
        Assert.Equal(["z t", "a q", "b r", "a s"], Ring(f, string.Empty, false).Zip(Passwords(f), (s, p) => $"{s} {p}"));

        var probe = new Probe();
        f.Replacekey(new Key { Attr = Attr.Parse("x=1"), Proto = probe }, false);
        Assert.Empty(probe.Closed);
        f.Replacekey(new Key { Attr = Attr.Parse("x=1"), Proto = probe }, false);
        Assert.Equal("x=1", Attr.Format(Assert.Single(probe.Closed).Attr));
    }

    [Fact]
    public void DisablekeyMarksAKeyDisabledOnce()
    {
        Factotum f = NewFactotum();
        var k = new Key { Attr = Attr.Parse("x=1") };
        f.Disablekey(k);
        f.Disablekey(k);
        Assert.Equal("x=1 disabled=by.factotum", Attr.Format(k.Attr));
    }

    [Fact]
    public async Task NoCapabilityIsMadeWithoutCaphash()
    {
        // Whatever descriptors the process has.
        for (int i = 0; i < 3; i++)
        {
            await init.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }

        Assert.Null(await NewFactotum().MkcapAsync("glenda", "none"));
    }

    [Fact]
    public void CtlTakesOneLineAndDelkeyWithoutAPatternDeletesEveryKeyButOnlyByPrivateQueries()
    {
        Factotum f = NewFactotum();
        Assert.Equal((-1, "multiline write not allowed"), (f.Ctlwrite("\nx", false), Factotum.Errstr));
        Assert.Equal(0, f.Ctlwrite("key proto=pass server=a user=u !password=p", false));
        Assert.Equal(0, f.Ctlwrite("key proto=pass server=b user=u !password=p", false));
        Assert.Equal((-1, "only !private? patterns are allowed for private fields"), (f.Ctlwrite("delkey server=a !password=p", false), Factotum.Errstr));
        Assert.Equal(0, f.Ctlwrite("delkey server=a !password?", false));
        Assert.Equal(["b"], Ring(f, string.Empty, false));
        Assert.Equal(0, f.Ctlwrite("delkey", false));
        Assert.Empty(Ring(f, string.Empty, false));
    }

    private static List<string> Ring(Factotum f, string user, bool usedisabled)
        => [.. Keys(f, user, usedisabled).Select(k => Attr.StrFind(k.Attr, "server")!)];

    private static List<string> Passwords(Factotum f)
        => [.. Keys(f, string.Empty, false).Select(k => Attr.StrFind(k.Privattr, "!password")!)];

    private static IEnumerable<Key> Keys(Factotum f, string user, bool usedisabled)
    {
        for (int i = 0; f.Findkey(out Key? k, new Keyinfo(new Fsstate(), null) { User = user, Noconf = true, Skip = i, Usedisabled = usedisabled }, null) == Rpc.Ok; i++)
        {
            yield return k!;
        }
    }

    private static string Error(byte[] reply)
    {
        Assert.Equal((byte)MessageTypes.Rerror, reply[4]);
        return new Rerror(reply).Ename;
    }

    private static byte[] Encode(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }

    private static (QidType Type, ulong Path) Qid(byte[] reply) => (new Rattach(reply).Qid.Type, new Rattach(reply).Qid.Path);

    private static (string Name, uint Mode, string Uid, uint Mtime) Stat(byte[] reply)
    {
        var stat = new Rstat(reply).Stat;
        Assert.Equal(stat.Uid, stat.Gid);
        Assert.Equal(stat.Mtime, stat.Atime);
        Assert.Equal(string.Empty, stat.Muid);
        return (stat.Name, stat.Mode, stat.Uid, stat.Mtime);
    }

    // What a read gave, or !error for one that failed.
    private static string Text(byte[] reply) => reply[4] switch
    {
        (byte)MessageTypes.Rerror => "!" + new Rerror(reply).Ename,
        (byte)MessageTypes.Rread => Encoding.UTF8.GetString(new Rread(reply).Data.Span),
        _ => string.Empty,
    };

    private static (QidType Type, ulong Path) Stat2(byte[] reply) => (new Rstat(reply).Stat.Qid.Type, new Rstat(reply).Stat.Qid.Path);

    // The processes there are besides init, as each test has only factotum's: the same ones as long
    // as none of them has died.
    // An Nvrsafe's sums made right.
    private static void Sum(NinePSharp.Fog.Commands.Nvrsafe safe)
    {
        safe.Machsum = NinePSharp.Fog.Commands.Authsrv.Nvcsum(safe.Machkey);
        safe.Configsum = NinePSharp.Fog.Commands.Authsrv.Nvcsum(safe.Config);
        safe.Authidsum = NinePSharp.Fog.Commands.Authsrv.Nvcsum(safe.Authid);
        safe.Authdomsum = NinePSharp.Fog.Commands.Authsrv.Nvcsum(safe.Authdom);
        safe.Aesmachsum = NinePSharp.Fog.Commands.Authsrv.Nvcsum(safe.Aesmachkey);
    }

    private long[] Serving() => [.. kernel.Processes.Keys.Where(pid => pid != init.Pid).Order()];

    // factotum run with the arguments, its errors to the descriptor given, and the status it exits with.
    private Task<string> RunAsync(int errors, params string[] arguments) => RunBoundAsync(null, "/", errors, arguments);

    // The same, in a namespace of its own where from, if given, replaces onto.
    private async Task<string> RunBoundAsync(string? from, string onto, int errors, params string[] arguments)
    {
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg | (from is null ? 0 : RforkFlags.Nameg), async child =>
        {
            if (from is not null)
            {
                await child.BindAsync(from, onto, NinePSharp.Namespaces.MountFlags.Replace);
            }

            for (int i = 0; i < 3; i++)
            {
                await child.DupAsync(await child.OpenAsync("/dev/null", NinePConstants.ORDWR), i);
            }

            if (errors >= 0)
            {
                await child.DupAsync(errors, 2);
            }

            await child.ExecAsync("/bin/auth/factotum", ["auth/factotum", .. arguments]);
        });
        return (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message;
    }

    // factotum -g run with the parameters, and the status it exits with.
    private Task<string> GetAsync(string parameters) => RunAsync(-1, "-g", parameters);

    // The line of /mnt/factotum/ctl that lists the key the line'th.
    private async Task<string> CtlLineAsync(int line)
    {
        int ctl = await init.OpenAsync("/mnt/factotum/ctl", NinePConstants.OREAD);
        string text = string.Empty;
        for (int i = 0; i < line; i++)
        {
            text = Encoding.UTF8.GetString((await init.ReadAsync(ctl, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);
        }

        await init.CloseAsync(ctl);
        return text;
    }

    // What /mnt/factotum/rpc says to the start given.
    private async Task<string> StartRpcAsync(string start)
    {
        int rpc = await init.OpenAsync("/mnt/factotum/rpc", NinePConstants.ORDWR);
        await init.WriteAsync(rpc, Encoding.UTF8.GetBytes(start));
        string read = Encoding.UTF8.GetString((await init.ReadAsync(rpc, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);
        await init.CloseAsync(rpc);
        return read;
    }

    // The passwords of the pass keys matching the pattern, as a client reads them.
    private async Task<List<string>> PasswordsAsync(string pattern)
    {
        int rpc = await init.OpenAsync("/mnt/factotum/rpc", NinePConstants.ORDWR);
        await init.WriteAsync(rpc, Encoding.UTF8.GetBytes($"start proto=pass role=client {pattern}"));
        Assert.Equal("ok", Encoding.UTF8.GetString((await init.ReadAsync(rpc, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span));
        await init.WriteAsync(rpc, "read"u8.ToArray());
        string read = Encoding.UTF8.GetString((await init.ReadAsync(rpc, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);
        await init.CloseAsync(rpc);
        return [read.Split(' ')[^1]];
    }

    private async Task<string> ReadTextAsync(string path)
    {
        int f = await init.OpenAsync(path, NinePConstants.OREAD);
        string text = Encoding.UTF8.GetString((await init.ReadAsync(f, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);
        await init.CloseAsync(f);
        return text;
    }

    private async Task WriteFileAsync(string path, string text)
    {
        int f = path.StartsWith("/dev/", StringComparison.Ordinal) ? await init.OpenAsync(path, NinePConstants.OWRITE) : await init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(f, Encoding.UTF8.GetBytes(text));
        await init.CloseAsync(f);
    }

    // What factotum wrote on its error output, its next writes.
    private async Task<string> ReadErrorsAsync(int errors, int writes = 1)
    {
        var text = new StringBuilder();
        for (int i = 0; i < writes; i++)
        {
            text.Append(Encoding.UTF8.GetString((await init.ReadAsync(errors, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span));
        }

        return text.ToString();
    }

    private Factotum NewFactotum()
    {
        Srv.Enter(init);
        return new Factotum(init, "auth/factotum");
    }

    // factotum started with its files posted as /srv/factotum.test, and that pipe opened.
    private Task StartAsync(params string[] arguments) => StartAsync(-1, arguments);

    // A pipe whose ends are none of init's 0, 1 and 2, which are held first.
    private async Task<(int Read, int Write)> PipeAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await init.FStatAsync(i);
            }
            catch (SyscallException)
            {
                await init.OpenAsync("/dev/null", NinePConstants.ORDWR);
            }
        }

        return await init.PipeAsync();
    }

    // factotum started as StartAsync starts it, with its errors on a pipe whose other end is returned.
    private async Task<int> StartWithErrorsAsync(params string[] arguments)
    {
        (int errors, int written) = await PipeAsync();
        await StartAsync(written, arguments);
        await init.CloseAsync(written);
        return errors;
    }

    private async Task StartAsync(int errors, string[] arguments)
    {
        // Its output and errors, which its debugging writes to, go nowhere unless errors is given.
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            for (int i = 0; i < 3; i++)
            {
                await child.DupAsync(await child.OpenAsync("/dev/null", NinePConstants.ORDWR), i);
            }

            if (errors >= 0)
            {
                await child.DupAsync(errors, 2);
            }

            await child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-s", "factotum.test", .. arguments]);
        });
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        fd = await init.OpenAsync("/srv/factotum.test", NinePConstants.ORDWR);
        Assert.Equal((byte)MessageTypes.Rversion, (await RpcAsync(t => new Tversion(t, 8216, "9P2000")))[4]);
    }

    // An attach of factotum's directory as the user.
    private async Task AttachAsync(uint fid, string user)
        => Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, fid, NoFid, user, "factotum")))[4]);

    private async Task<byte[]> OpenAsync(uint from, uint fid, string name, byte mode)
    {
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, from, fid, [name])))[4]);
        return await RpcAsync(t => new Topen(t, fid, mode));
    }

    private async Task<List<string>> ListAsync(uint fid)
    {
        var names = new List<string>();
        for (ulong offset = 0; ;)
        {
            var data = new Rread(await RpcAsync(t => new Tread(t, fid, offset, 8192))).Data;
            if (data.IsEmpty)
            {
                return names;
            }

            for (int at = 0; at < data.Length;)
            {
                names.Add(new NinePSharp.Messages.Stat(data.Span, ref at).Name);
            }

            offset += (ulong)data.Length;
        }
    }

    private async Task<List<string>> NamesAsync(string path)
    {
        int f = await init.OpenAsync(path, NinePConstants.OREAD);
        List<string> names = [.. (await init.DirReadAsync(f)).Select(e => e.Name)];
        await init.CloseAsync(f);
        return names;
    }

    private async Task<uint> OpenFidAsync(uint from, uint fid, string name, byte mode)
    {
        Assert.Equal((byte)MessageTypes.Ropen, (await OpenAsync(from, fid, name, mode))[4]);
        return fid;
    }

    private async Task WriteAsync(uint fid, string text)
    {
        byte[] reply = await RpcAsync(t => new Twrite(t, fid, 0, Encoding.UTF8.GetBytes(text)));
        Assert.Equal(((byte)MessageTypes.Rwrite, string.Empty), (reply[4], Text(reply)));
    }

    // An rpc written and its answer read.
    private async Task<string> RpcCallAsync(uint fid, string rpc, uint count = 8192)
    {
        await WriteAsync(fid, rpc);
        return Text(await RpcAsync(t => new Tread(t, fid, 0, count)));
    }

    private async Task<byte[]> RpcStartAsync(uint fid, string rpc)
    {
        await WriteAsync(fid, rpc);
        return await RpcAsync(t => new Tread(t, fid, 0, 8192));
    }

    private async Task<ushort> SendAsync(Func<ushort, ISerializable> request)
    {
        ushort t = ++tag;
        await init.WriteAsync(fd, Encode(request(t)));
        return t;
    }

    // The replies to the tags, in the order of the tags, whatever order they came in.
    private async Task<string[]> RepliesAsync(params ushort[] tags)
    {
        var replies = new Dictionary<ushort, string>();
        while (replies.Count < tags.Length)
        {
            byte[] reply = (await init.ReadAsync(fd, 8216).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
            replies[BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5))] = Text(reply);
        }

        return [.. tags.Select(t => replies[t])];
    }

    private async Task<byte[]> RpcAsync(Func<ushort, ISerializable> request)
    {
        ushort t = ++tag;
        await init.WriteAsync(fd, Encode(request(t)));
        byte[] reply = (await init.ReadAsync(fd, 8216).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
        Assert.Equal(t, BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5)));
        return reply;
    }

    // A protocol that keeps the keys closed on it.
    private sealed class Probe : NinePSharp.Fog.Commands.Proto
    {
        public List<Key> Closed { get; } = [];

        public override string Name => "probe";

        public override Task<Rpc> InitAsync(Factotum f, Fsstate fss) => Task.FromResult(Rpc.Ok);

        public override void Closekey(Key k) => Closed.Add(k);

        public override Task<Rpc> WriteAsync(Factotum f, Fsstate fss, byte[] a) => Task.FromResult(Rpc.Ok);

        public override Task<(Rpc Ret, byte[] A)> ReadAsync(Factotum f, Fsstate fss, int n) => Task.FromResult((Rpc.Ok, Array.Empty<byte>()));

        public override void Close(Fsstate fss)
        {
        }
    }
}
