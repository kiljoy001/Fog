using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// lib9p's srv as a server built on it sees it: a root holding the file f and the directory d, served
// by functions each test sets, posted in /srv and spoken to in 9P. What factotum and mntgen do not
// ask of lib9p is asked here: auth, create, remove and wstat, the end of a connection, the processes
// srvrelease makes, and when a fid is let go while requests hold it.
public sealed class Lib9pTests : IAsyncLifetime
{
    private const uint NoFid = uint.MaxValue;
    private static readonly Qid Root = new(QidType.QTDIR, 0, 0);
    private static readonly Qid File = new(QidType.QTFILE, 0, 1);
    private static readonly Qid Dir = new(QidType.QTDIR, 0, 2);
    private readonly FakeHostNetwork host = new();
    private readonly FakeHostConsole console = new();
    private readonly ManualClock clock = new() { Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
    private readonly List<string> seen = [];
    private readonly List<(Req R, Process Up, TaskCompletionSource Done)> parked = [];
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FogKernel kernel = null!;
    private Process init = null!;
    private Srv srv = null!;
    private int fd = -1;
    private ushort tag;

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", clock, host, console);
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
    public async Task AServerWithoutItsFunctionsAttachesAndOpensAndRefusesTheRest()
    {
        await StartAsync(new Srv { Walk1 = Walk1 });
        Assert.Equal(": authentication not required", Error(await RpcAsync(t => new Tauth(t, 9, "glenda", string.Empty))));
        Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, 1, NoFid, "glenda", string.Empty)))[4]);

        // Without its attach, a server's root is no directory, so nothing walks from it.
        Assert.Equal("walk in non-directory", Error(await RpcAsync(t => new Twalk(t, 1, 2, ["f"]))));
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 2, [])))[4]);
        var open = new Ropen(await RpcAsync(t => new Topen(t, 2, NinePConstants.ORDWR)));
        Assert.Equal((0ul, 0u), (open.Qid.Path, open.Iounit));
        Assert.Equal("no srv->read", Error(await RpcAsync(t => new Tread(t, 2, 0, 10))));
        Assert.Equal("write prohibited", Error(await RpcAsync(t => new Twrite(t, 2, 0, "x"u8.ToArray()))));
        Assert.Equal("stat prohibited", Error(await RpcAsync(t => new Tstat(t, 2))));
        Assert.Equal("create in non-directory", Error(await RpcAsync(t => new Tcreate(t, 1, "x", 0, NinePConstants.OREAD))));
        Assert.Equal("remove prohibited", Error(await RpcAsync(t => new Tremove(t, 2))));
    }

    [Fact]
    public async Task ARequestRefusedBeforeItsFunctionIsAnsweredOnceAndTheServerGoesOn()
    {
        await StartAsync(Server(park: true));
        await AttachAsync(1);
        Assert.Equal("is a directory", Error(await RpcAsync(t => new Topen(t, 1, NinePConstants.ORDWR))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tremove(t, 8))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Twstat(t, 8, Process.NullDir))));
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        ushort read = await SendAsync(t => new Tread(t, 2, 0, 10));
        await WaitForAsync(() => parked.Count == 1);
        await init.WriteAsync(fd, Encode(new Tstat(read, 2)));
        Assert.Equal(["!duplicate tag"], await RepliesAsync(read));
        Assert.Equal((byte)MessageTypes.Rstat, (await RpcAsync(t => new Tstat(t, 1)))[4]);
    }

    [Fact]
    public async Task AuthGivesAnAfidOpenForReadingAndWritingOrNoneWhenItFails()
    {
        await StartAsync(Server(auth: async r =>
        {
            r.Qid = new Qid(QidType.QTAUTH, 0, 9);
            r.Afid!.Qid = r.Qid;
            await r.Srv.RespondAsync(r, ((Tauth)r.Ifcall).Uname == "bad" ? "who?" : null);
        }));
        Assert.Equal(QidType.QTAUTH, new Rauth(await RpcAsync(t => new Tauth(t, 5, "glenda", string.Empty))).Aqid.Type);
        Assert.Equal("auth read", Text(await RpcAsync(t => new Tread(t, 5, 0, 10))));
        Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, 1, 5, "glenda", string.Empty)))[4]);
        Assert.Contains("attach afid 5", seen);
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 5)))[4]);
        Assert.Equal([5u], Destroyed());
        Assert.Equal("who?", Error(await RpcAsync(t => new Tauth(t, 6, "bad", string.Empty))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tattach(t, 2, 6, "glenda", string.Empty))));
        Assert.Equal((byte)MessageTypes.Rauth, (await RpcAsync(t => new Tauth(t, 6, "glenda", string.Empty)))[4]);
    }

    [Fact]
    public async Task AnAttachTheServerRefusesLeavesItsFidFree()
    {
        await StartAsync(Server(attach: r => r.Srv.RespondAsync(r, ((Tattach)r.Ifcall).Uname == "bad" ? "no" : null)));
        Assert.Equal("no", Error(await RpcAsync(t => new Tattach(t, 1, NoFid, "bad", string.Empty))));
        Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, 1, NoFid, "glenda", string.Empty)))[4]);
        Assert.Contains("open as glenda", await OpenSeenAsync(1));
    }

    [Fact]
    public async Task CreateMakesItsFidTheFileOpenInTheModeAskedWithTheIounitThereIsRoomFor()
    {
        await StartAsync(Server());
        await AttachAsync(1);
        Assert.Equal("no", Error(await RpcAsync(t => new Tcreate(t, 1, "other", 0b110_110_110, NinePConstants.OWRITE))));
        var created = new Rcreate(await RpcAsync(t => new Tcreate(t, 1, "x", 0b110_110_110, NinePConstants.OWRITE)));
        Assert.Equal((9ul, 8192u), (created.Qid.Path, created.Iounit));
        Assert.Equal(3u, new Rwrite(await RpcAsync(t => new Twrite(t, 1, 0, "abc"u8.ToArray()))).Count);
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Tread(t, 1, 0, 10))));
        Assert.Equal("9P protocol botch", Error(await RpcAsync(t => new Tcreate(t, 1, "x", 0, NinePConstants.OWRITE))));
    }

    [Fact]
    public async Task RemoveClunksItsFidWhateverTheServerSays()
    {
        await StartAsync(Server());
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        await WalkAsync(1, 3, "d");
        Assert.Equal((byte)MessageTypes.Rremove, (await RpcAsync(t => new Tremove(t, 2)))[4]);
        Assert.Equal("not a file", Error(await RpcAsync(t => new Tremove(t, 3))));
        Assert.Equal(["remove 1", "remove 2"], seen.Where(s => s.StartsWith("remove", StringComparison.Ordinal)));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tstat(t, 2))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tstat(t, 3))));
        Assert.Equal([2u, 3u], Destroyed());
    }

    [Fact]
    public async Task WstatRefusesAChangeOfQidOrTypeAndModeBitsItDoesNotKnow()
    {
        await StartAsync(Server());
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Stat none = Process.NullDir;
        Stat Changed(Qid qid, uint mode) => new(0, none.Type, none.Dev, qid, mode, none.Atime, none.Mtime, none.Length, "g", string.Empty, string.Empty, string.Empty);
        Assert.Equal("wstat -- attempt to change qid.path", Error(await RpcAsync(t => new Twstat(t, 2, Changed(new Qid((QidType)0xFF, uint.MaxValue, 7), uint.MaxValue)))));
        Assert.Equal("wstat -- attempt to change qid.vers", Error(await RpcAsync(t => new Twstat(t, 2, Changed(new Qid((QidType)0xFF, 3, ulong.MaxValue), uint.MaxValue)))));
        Assert.Equal("wstat -- unknown bits in mode", Error(await RpcAsync(t => new Twstat(t, 2, Changed(none.Qid, 0x1000)))));
        Assert.Equal("wstat -- qid.type/mode mismatch", Error(await RpcAsync(t => new Twstat(t, 2, Changed(new Qid(QidType.QTDIR, uint.MaxValue, ulong.MaxValue), 0b110_000_000)))));
        Assert.Equal("wstat -- attempt to change qid.type", Error(await RpcAsync(t => new Twstat(t, 2, Changed(none.Qid, 0x80000000u | 0b110_000_000)))));
        Assert.Equal("wstat -- attempt to change qid.type", Error(await RpcAsync(t => new Twstat(t, 2, Changed(new Qid(QidType.QTDIR, uint.MaxValue, ulong.MaxValue), uint.MaxValue)))));
        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Twstat(t, 2, Changed(new Qid((QidType)0xFF, uint.MaxValue, ulong.MaxValue), 0x40000000u | 0x20000000u | 0x04000000u | 0b110_000_000)))));
        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Twstat(t, 2, Changed(none.Qid, uint.MaxValue)))));
        await WalkAsync(1, 3, "d");
        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Twstat(t, 3, Changed(new Qid(QidType.QTDIR, uint.MaxValue, ulong.MaxValue), 0x80000000u | 0b111_101_101)))));
        Assert.Equal(["wstat g", "wstat g", "wstat g"], seen.Where(s => s.StartsWith("wstat", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ReadsAndWritesTakeAtMostWhatTheMessageSizeLeavesRoomFor()
    {
        await StartAsync(Server());
        Assert.Equal(256u, new Rversion(await RpcAsync(t => new Tversion(t, 256, "9P2000"))).MSize);
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.ORDWR)))[4]);
        Assert.Equal(232, new Rread(await RpcAsync(t => new Tread(t, 2, 0, 1000))).Data.Length);
        Assert.Empty(new Rread(await RpcAsync(t => new Tread(t, 2, 0, 0))).Data.ToArray());
        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Twrite(t, 2, 0, new byte[233]))));
        Assert.Equal("write 232", seen[^1]);
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 3, ["d"])))[4]);
        var open = new Ropen(await RpcAsync(t => new Topen(t, 3, NinePConstants.OREAD)));

        // lib9p only lowers an iounit; one the server gives none of stays none.
        Assert.Equal(0u, open.Iounit);
    }

    [Fact]
    public async Task ADirectoryIsReadFromItsStartOrWhereTheLastReadEndedAsMuchAsFits()
    {
        await StartAsync(Server());
        await AttachAsync(1);
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 1, NinePConstants.OREAD)))[4]);
        int first = StatOf("f").Size;
        byte[] one = new Rread(await RpcAsync(t => new Tread(t, 1, 0, (uint)first))).Data.ToArray();
        Assert.Equal(["f"], Names(one));
        byte[] rest = new Rread(await RpcAsync(t => new Tread(t, 1, (ulong)one.Length, 8192))).Data.ToArray();
        Assert.Equal(["d"], Names(rest));
        Assert.Empty(new Rread(await RpcAsync(t => new Tread(t, 1, (ulong)(one.Length + rest.Length), 8192))).Data.ToArray());
        Assert.Equal(["f", "d"], Names(new Rread(await RpcAsync(t => new Tread(t, 1, 0, 8192))).Data.ToArray()));
        Assert.Empty(new Rread(await RpcAsync(t => new Tread(t, 1, 0, (uint)first - 1))).Data.ToArray());
    }

    [Fact]
    public async Task AWalkFailsOnlyWhenItWalksNothingAndItsNewFidIsLetGoThen()
    {
        await StartAsync(Server());
        await AttachAsync(1);
        Assert.Equal("file not found", Error(await RpcAsync(t => new Twalk(t, 1, 2, ["nonesuch"]))));
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 3, ["d", "nonesuch"]))).Wqid);
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tstat(t, 3))));
        Assert.Equal("cannot clone", Error(await RpcAsync(t => new Twalk(t, 1, 4, ["noclone"]))));
        Assert.Equal("unknown fid", Error(await RpcAsync(t => new Tstat(t, 4))));
        await StartAsync(Server(walk: r => r.Srv.RespondAsync(r, null)));
        await AttachAsync(1);
        Assert.Equal("file not found", Error(await RpcAsync(t => new Twalk(t, 1, 2, ["f"]))));
        Assert.Empty(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 3, []))).Wqid);
        Assert.Equal((Root.Type, Root.Path), StatQid(await RpcAsync(t => new Tstat(t, 3))));
    }

    [Fact]
    public async Task AFidIsLetGoOnlyWhenTheLastRequestHoldingItIsAnswered()
    {
        await StartAsync(Server(park: true));
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        ushort read = await SendAsync(t => new Tread(t, 2, 0, 10));
        await WaitForAsync(() => parked.Count == 1);
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 2)))[4]);
        Assert.Empty(Destroyed());
        await RespondParkedAsync();
        Assert.Equal(["parked"], await RepliesAsync(read));
        Assert.Equal([2u], Destroyed());
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 1, ["f"]))).Wqid);
        Assert.Equal([2u], Destroyed());
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 1)))[4]);
        Assert.Equal([2u, 1u], Destroyed());
    }

    [Fact]
    public async Task AFlushWaitsForTheAnswerOfWhatItFlushesAndHoldsItTillThen()
    {
        await StartAsync(Server(park: true));
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        ushort read = await SendAsync(t => new Tread(t, 2, 0, 10));
        await WaitForAsync(() => parked.Count == 1);
        ushort flush = await SendAsync(t => new Tflush(t, read));
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 2)))[4]);
        await RespondParkedAsync();
        Assert.Equal(["parked", string.Empty], await RepliesAsync(read, flush));
        Assert.Equal([2u], Destroyed());
        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Tflush(t, 999))));

        // A flush of its own tag waits for its own answer, in lib9p's rflush as here, and gets none.
        ushort self = await SendAsync(t => new Tflush(t, t));
        Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, 7, NoFid, "glenda", string.Empty)))[4]);
        Assert.Equal(self + 1, tag);
    }

    [Fact]
    public async Task AFlushedRequestLetsGoOfItsFidOnceSoTheFidLivesOn()
    {
        await StartAsync(Server(park: true));
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        ushort read = await SendAsync(t => new Tread(t, 2, 0, 10));
        await WaitForAsync(() => parked.Count == 1);
        ushort flush = await SendAsync(t => new Tflush(t, read));
        await Task.Delay(100);
        await RespondParkedAsync();
        Assert.Equal(["parked", string.Empty], await RepliesAsync(read, flush));
        Assert.Empty(Destroyed());
        Assert.Equal((byte)MessageTypes.Rstat, (await RpcAsync(t => new Tstat(t, 2)))[4]);
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 2)))[4]);
        Assert.Equal([2u], Destroyed());
    }

    [Fact]
    public async Task AFlushOfWhatIsWaitingGoesToTheServerAndOfNothingDoesNot()
    {
        await StartAsync(Server(park: true, flush: r =>
        {
            seen.Add($"flush {((Tflush)r.Ifcall).OldTag}");
            return r.Srv.RespondAsync(r, null);
        }));
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        Assert.Equal(string.Empty, Text(await RpcAsync(t => new Tflush(t, 999))));
        ushort read = await SendAsync(t => new Tread(t, 2, 0, 10));
        await WaitForAsync(() => parked.Count == 1);
        ushort flush = await SendAsync(t => new Tflush(t, read));
        await WaitForAsync(() => seen.Contains($"flush {read}"));
        await RespondParkedAsync();
        Assert.Equal(["parked", string.Empty], await RepliesAsync(read, flush));
        Assert.DoesNotContain("flush 999", seen);
    }

    [Fact]
    public async Task VersionIsAnsweredOnceNothingElseIsWaitingAndTakesAMessageSizeOf256()
    {
        await StartAsync(Server());
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal(256u, new Rversion(await RpcAsync(t => new Tversion(t, 256, "9P2000"))).MSize);
    }

    [Fact]
    public async Task AServerThatReleasesItsLockGoesOnReadingInAnotherProcessAndKeepsAFewOfThem()
    {
        await StartAsync(Server(park: true, release: true));
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        int before = kernel.Processes.Count;
        var reads = new List<ushort>();
        for (int i = 0; i < 10; i++)
        {
            reads.Add(await SendAsync(t => new Tread(t, 2, 0, 10)));
            int n = i + 1;
            await WaitForAsync(() => parked.Count == n);
        }

        Assert.Equal((byte)MessageTypes.Rstat, (await RpcAsync(t => new Tstat(t, 2)))[4]);
        Assert.Equal(before + 10, kernel.Processes.Count);
        await RespondParkedAsync();
        Assert.All(await RepliesAsync([.. reads]), text => Assert.Equal("parked", text));
        Assert.Equal((byte)MessageTypes.Rstat, (await RpcAsync(t => new Tstat(t, 2)))[4]);

        // srvwork's processes beyond the first leave once more than eight hold the server.
        await WaitForAsync(() => kernel.Processes.Count == before + 7);
        Assert.Equal((byte)MessageTypes.Rstat, (await RpcAsync(t => new Tstat(t, 2)))[4]);
    }

    [Fact]
    public async Task TheServerEndsWhenItsConnectionDoesAndTellsTheProgram()
    {
        await StartAsync(Server(), name: null);
        await AttachAsync(1);
        await init.CloseAsync(fd);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OnlyTheLastProcessServingTellsTheProgramOfTheEnd()
    {
        await StartAsync(Server(park: true, release: true), name: null);
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        int before = kernel.Processes.Count;
        ushort read = await SendAsync(t => new Tread(t, 2, 0, 10));
        await WaitForAsync(() => parked.Count == 1);
        await RespondParkedAsync();
        Assert.Equal(["parked"], await RepliesAsync(read));
        Assert.Equal(before + 1, kernel.Processes.Count);

        // The parked read's process takes the server again before the end, or both would see themselves last, in srvwork as here.
        await Task.Delay(100);
        await init.CloseAsync(fd);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => kernel.Processes.Count == before - 1);
        Assert.Equal(1, seen.Count(s => s == "end"));
    }

    [Fact]
    public async Task AMessageWhoseLengthCannotBeOnesEndsTheServer()
    {
        await StartAsync(Server());
        await init.WriteAsync(fd, new byte[] { 3, 0, 0, 0, 100, 0, 0 });
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARequestAnsweredTwiceIsAnError()
    {
        await StartAsync(Server(open: async r =>
        {
            await r.Srv.RespondAsync(r, null);
            try
            {
                await r.Srv.RespondAsync(r, null);
            }
            catch (InvalidOperationException error)
            {
                seen.Add(error.Message);
            }
        }));
        await AttachAsync(1);
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 1, NinePConstants.OREAD)))[4]);
        await WaitForAsync(() => seen.Contains("respond: already responded"));
    }

    [Fact]
    public async Task PostsrvRunsTheServerInANamespaceAndNoteGroupOfItsOwnAndLeavesThePosterOnlyItsEnd()
    {
        var posted = new TaskCompletionSource<(int Fd, string Fds, string? Waited, long NoteId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        srv = Server();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            Srv.Enter(child);
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await child.CloseAsync(i);
                }
                catch (SyscallException)
                {
                    // Not open.
                }
            }

            int end = await srv.PostAsync("lib9p.test");
            string? waited = null;
            try
            {
                await child.WaitAsync();
            }
            catch (SyscallException error)
            {
                waited = error.Message;
            }

            await child.BindAsync("/dev", "/tmp", MountFlags.Replace);
            posted.SetResult((end, await ReadFdsAsync(child), waited, child.NoteId));
        });
        (int end, string fds, string? waited, long noteid) = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("no living children", waited);
        Assert.Equal([end], fds.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(line => int.Parse(line.TrimStart().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)).Where(n => n != end + 1));
        fd = await init.OpenAsync("/srv/lib9p.test", NinePConstants.ORDWR);
        await AttachAsync(1);
        await WalkAsync(1, 2, "f");
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        Assert.Contains(seen, s => s.StartsWith("open noteid ", StringComparison.Ordinal) && s != $"open noteid {noteid}");
        Assert.Contains("open tmp has tmp", seen);
    }

    [Fact]
    public async Task PostsrvFailsWithTheErrorOfPostingAndPostmountsrvSaysSo()
    {
        await StartAsync(Server());
        var failed = new TaskCompletionSource<(int Fd, string Error, string Mounted)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = Server();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            Srv.Enter(child);
            string fds = await ReadFdsAsync(child);
            int end = await other.PostAsync("lib9p.test");
            Assert.Equal(fds, await ReadFdsAsync(child));
            string mounted;
            try
            {
                await other.PostMountAsync("lib9p.test", "/n", MountFlags.After);
                mounted = "mounted";
            }
            catch (SyscallException error)
            {
                mounted = error.Message;
            }

            failed.SetResult((end, other.Errstr ?? string.Empty, mounted));
        });
        (int end, string error, string mounted) = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((-1, "file already exists: '/srv/lib9p.test'"), (end, error));
        Assert.Equal("postsrv: file already exists: '/srv/lib9p.test'", mounted);
    }

    [Fact]
    public async Task PostmountsrvWithoutAMountPointClosesWhatItPosted()
    {
        srv = Server();
        var posted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            Srv.Enter(child);
            await srv.PostMountAsync("lib9p.test", null, 0);
            posted.SetResult(await ReadFdsAsync(child));
        });
        Assert.DoesNotContain("#|", await posted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        fd = await init.OpenAsync("/srv/lib9p.test", NinePConstants.ORDWR);
        await AttachAsync(1);
    }

    private static Stat StatOf(string name) => new(0, 0, 0, name == "f" ? File : Dir, name == "f" ? 0b110_100_100u : 0x80000000u | 0b111_101_101, 0, 0, 0, name, "glenda", "glenda", string.Empty);

    private static string? Walk1(Fid fid, string name)
    {
        if (fid.Qid.Path == 0 && name is "f" or "d" or "..")
        {
            fid.Qid = name == "f" ? File : name == "d" ? Dir : Root;
            return null;
        }

        return fid.Qid.Path == 2 && name == ".." ? Set(fid, Root) : "file not found";
    }

    private static string? Set(Fid fid, Qid qid)
    {
        fid.Qid = qid;
        return null;
    }

    private static string[] Names(byte[] data)
    {
        var names = new List<string>();
        for (int at = 0; at < data.Length;)
        {
            names.Add(new Stat(data, ref at).Name);
        }

        return [.. names];
    }

    private static string Error(byte[] reply)
    {
        Assert.Equal((byte)MessageTypes.Rerror, reply[4]);
        return new Rerror(reply).Ename;
    }

    private static string Text(byte[] reply) => reply[4] switch
    {
        (byte)MessageTypes.Rerror => "!" + new Rerror(reply).Ename,
        (byte)MessageTypes.Rread => Encoding.UTF8.GetString(new Rread(reply).Data.Span),
        _ => string.Empty,
    };

    private static (QidType Type, ulong Path) StatQid(byte[] reply) => (new Rstat(reply).Stat.Qid.Type, new Rstat(reply).Stat.Qid.Path);

    private static byte[] Encode(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }

    private static async Task<string> ReadFdsAsync(Process process)
    {
        int f = await process.OpenAsync($"/proc/{process.Pid}/fd", NinePConstants.OREAD);
        var text = new StringBuilder();
        for (ReadOnlyMemory<byte> b; !(b = await process.ReadAsync(f, 8192)).IsEmpty;)
        {
            text.Append(Encoding.UTF8.GetString(b.Span));
        }

        await process.CloseAsync(f);
        return text.ToString();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private List<uint> Destroyed() => [.. seen.Where(s => s.StartsWith("destroy ", StringComparison.Ordinal)).Select(s => uint.Parse(s[8..], System.Globalization.CultureInfo.InvariantCulture))];

    private async Task<List<string>> OpenSeenAsync(uint fid)
    {
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, fid, NinePConstants.OREAD)))[4]);
        return seen;
    }

    // The test's server: its functions record what they are asked, and a read parks its request
    // when park is set, after letting the server go on reading when release is set too.
    private Srv Server(Func<Req, Task>? attach = null, Func<Req, Task>? auth = null, Func<Req, Task>? walk = null, Func<Req, Task>? flush = null, Func<Req, Task>? open = null, bool park = false, bool release = false)
    {
        Srv? s = null;
        s = new Srv
        {
            Attach = attach ?? (r =>
            {
                if (r.Afid is { } afid)
                {
                    seen.Add($"attach afid {afid.Number}");
                }

                r.Qid = Root;
                r.Fid!.Qid = Root;
                return s!.RespondAsync(r, null);
            }),
            Auth = auth,
            Walk1 = walk is null ? Walk1 : null,
            Walk = walk,
            Clone = (fid, newfid) => newfid.Number == 4 ? "cannot clone" : null,
            Open = open ?? (async r =>
            {
                seen.Add($"open as {r.Fid!.Uid}");
                seen.Add($"open noteid {Srv.Up.NoteId}");
                try
                {
                    await Srv.Up.StatAsync("/tmp/null");
                    seen.Add("open tmp has dev");
                }
                catch (SyscallException)
                {
                    seen.Add("open tmp has tmp");
                }

                await s!.RespondAsync(r, null);
            }),
            Create = r =>
            {
                if (((Tcreate)r.Ifcall).Name != "x")
                {
                    return s!.RespondAsync(r, "no");
                }

                (r.Qid, r.Iounit) = (new Qid(QidType.QTFILE, 0, 9), uint.MaxValue);
                return s!.RespondAsync(r, null);
            },
            Read = async r =>
            {
                if (r.Fid!.Qid.Type == QidType.QTAUTH)
                {
                    r.Data = "auth read"u8.ToArray();
                    await s!.RespondAsync(r, null);
                }
                else if ((r.Fid.Qid.Type & QidType.QTDIR) != 0)
                {
                    Stat[] entries = [StatOf("f"), StatOf("d")];
                    Srv.Dirread(r, i => i < entries.Length ? entries[i] : null);
                    await s!.RespondAsync(r, null);
                }
                else if (park)
                {
                    if (release)
                    {
                        await s!.ReleaseAsync();
                    }

                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (parked)
                    {
                        parked.Add((r, Srv.Up, done));
                    }

                    if (release)
                    {
                        await done.Task;
                        await s!.AcquireAsync();
                    }
                }
                else
                {
                    r.Data = new byte[r.Count];
                    await s!.RespondAsync(r, null);
                }
            },
            Write = r =>
            {
                seen.Add($"write {r.Data.Length}");
                r.Rcount = r.Count;
                return s!.RespondAsync(r, null);
            },
            Remove = r =>
            {
                seen.Add($"remove {r.Fid!.Qid.Path}");
                return s!.RespondAsync(r, r.Fid.Qid.Path == 2 ? "not a file" : null);
            },
            Stat = r =>
            {
                r.D = r.Fid!.Qid.Path == 0 ? new Stat(0, 0, 0, Root, 0x80000000u | 0b111_101_101, 0, 0, 0, "/", "glenda", "glenda", string.Empty) : StatOf(r.Fid.Qid.Path == 1 ? "f" : "d");
                return s!.RespondAsync(r, null);
            },
            Wstat = r =>
            {
                seen.Add($"wstat {r.D.Name}");
                return s!.RespondAsync(r, null);
            },
            Flush = flush,
            DestroyFid = fid => seen.Add($"destroy {fid.Number}"),
            End = _ =>
            {
                seen.Add("end");
                ended.TrySetResult();
                return Task.CompletedTask;
            },
        };
        return s;
    }

    private async Task RespondParkedAsync()
    {
        (Req R, Process Up, TaskCompletionSource Done)[] waiting;
        lock (parked)
        {
            waiting = [.. parked];
            parked.Clear();
        }

        // Each answered as the server's process that took it, through the server's descriptor.
        foreach ((Req r, Process up, TaskCompletionSource done) in waiting)
        {
            Srv.Enter(up);
            r.Data = "parked"u8.ToArray();
            await r.Srv.RespondAsync(r, null);
            done.SetResult();
        }

        Srv.Enter(init);
    }

    // The server posted as /srv/lib9p.test, or with nothing posted and its end kept, and that end opened.
    private async Task StartAsync(Srv server, string? name = "lib9p.test")
    {
        if (fd >= 0)
        {
            await init.CloseAsync(fd);
            await init.RemoveAsync("/srv/lib9p.test");
            seen.Clear();
            tag = 0;
        }

        srv = server;
        var posted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        init.Fork(RforkFlags.Proc, async child =>
        {
            Srv.Enter(child);
            posted.SetResult(await srv.PostAsync(name));
        });
        int end = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fd = name is null ? end : await init.OpenAsync($"/srv/{name}", NinePConstants.ORDWR);
        if (name is not null)
        {
            await init.CloseAsync(end);
        }
    }

    private async Task AttachAsync(uint fid)
        => Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, fid, NoFid, "glenda", string.Empty)))[4]);

    private async Task WalkAsync(uint from, uint fid, string name)
        => Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, from, fid, [name])))[4]);

    private async Task<ushort> SendAsync(Func<ushort, ISerializable> request)
    {
        ushort t = ++tag;
        await init.WriteAsync(fd, Encode(request(t)));
        return t;
    }

    private async Task<byte[]> RpcAsync(Func<ushort, ISerializable> request)
    {
        ushort t = await SendAsync(request);
        byte[] reply = (await init.ReadAsync(fd, 8216).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
        Assert.Equal(t, BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5)));
        return reply;
    }

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
}
