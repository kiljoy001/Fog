using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// ndb/cs's file server as cs.c serves it, spoken to on the pipe it posts in /srv: the messages a
// mount of it never sends, the requests that only a client timing its messages can make, and what
// cs makes of the host's resolver and interfaces.
public sealed class CsTests : IAsyncLifetime
{
    private const string Database = "sys=fogtest ip=192.168.1.10\nsys=ilhost ip=10.2.2.2 proto=il\ntcp=9fs port=564 restricted\n";
    private const uint NoFid = uint.MaxValue;
    private readonly FakeHostNetwork host = new();
    private readonly FakeHostConsole console = new();
    private readonly ManualClock clock = new() { Now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero) };
    private FogKernel kernel = null!;
    private Process init = null!;
    private int fd = -1;
    private ushort tag;

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", clock, host, console);
        init = await kernel.BootAsync();
        await WriteFileAsync("/tmp/ndb", Database);
    }

    public Task DisposeAsync()
    {
        host.Gate = null;
        host.InterfacesGate?.Set();
        foreach (Process process in kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task VersionGivesTheSmallerMsizeAnd9P2000ForA9PVersionOrUnknown()
    {
        await StartAsync();
        var big = new Rversion(await RpcAsync(t => new Tversion(t, 65536, "9P2000")));
        Assert.Equal((8216u, "9P2000"), (big.MSize, big.Version));
        var small = new Rversion(await RpcAsync(t => new Tversion(t, 4096, "9P2000.u")));
        Assert.Equal((4096u, "9P2000"), (small.MSize, small.Version));
        var exact = new Rversion(await RpcAsync(t => new Tversion(t, 8216, "XP2000")));
        Assert.Equal((8216u, "unknown"), (exact.MSize, exact.Version));
    }

    [Fact]
    public async Task AuthIsNotRequiredAndAFlushIsAnswered()
    {
        await StartAsync();
        Assert.Equal("cs: authentication not required", Error(await RpcAsync(t => new Tauth(t, 5, "glenda", string.Empty))));
        Assert.Equal((byte)MessageTypes.Rflush, (await RpcAsync(t => new Tflush(t, 999)))[4]);
    }

    [Fact]
    public async Task AWalkFromTheDirectoryReachesCsAndItsDotsAndNothingElse()
    {
        await StartAsync();
        Qid root = new Rattach(await RpcAsync(t => new Tattach(t, 1000, NoFid, "bob", string.Empty))).Qid;
        Assert.Equal((QidType.QTDIR, 0ul), (root.Type, root.Path));

        Qid[] cs = new Rwalk(await RpcAsync(t => new Twalk(t, 1000, 1001, ["cs"]))).Wqid;
        Assert.Equal((QidType.QTFILE, 1ul), (Assert.Single(cs).Type, cs[0].Path));
        Qid[] dots = new Rwalk(await RpcAsync(t => new Twalk(t, 1000, 1002, ["..", ".", "cs"]))).Wqid;
        Assert.Equal([(QidType.QTDIR, 0ul), (QidType.QTDIR, 0ul), (QidType.QTFILE, 1ul)], dots.Select(q => (q.Type, q.Path)));
        Assert.Equal("cs: not a directory", Error(await RpcAsync(t => new Twalk(t, 1002, 1003, [".."]))));

        Assert.Equal("cs: not a directory", Error(await RpcAsync(t => new Twalk(t, 1001, 1003, ["cs"]))));
        Assert.Equal("cs: not a directory", Error(await RpcAsync(t => new Twalk(t, 1000, 1003, ["cs", "cs"]))));
        Assert.Equal("cs: file does not exist", Error(await RpcAsync(t => new Twalk(t, 1000, 1003, ["nothing"]))));
        Assert.Equal("cs: clone to used channel", Error(await RpcAsync(t => new Twalk(t, 1000, 1001, ["cs"]))));

        // A failed walk leaves the new fid free, and so does one past the 16 names a walk takes.
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1000, 1003, ["cs"]))).Wqid);
        Assert.Equal(16, new Rwalk(await RpcAsync(t => new Twalk(t, 1000, 1004, [.. Enumerable.Repeat(".", 17)]))).Wqid.Length);
        Assert.Empty(new Rwalk(await RpcAsync(t => new Twalk(t, 1000, 1004, []))).Wqid);

        // A walk of the fid itself moves it.
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1000, 1000, ["cs"]))).Wqid);
        Assert.Equal("cs: not a directory", Error(await RpcAsync(t => new Twalk(t, 1000, 1000, ["cs"]))));
    }

    [Fact]
    public async Task StatAndReadingTheDirectoryGiveCsTheAttachersAtTheHostsTime()
    {
        await StartAsync();
        await RpcAsync(t => new Tattach(t, 1000, NoFid, "bob", string.Empty));
        await RpcAsync(t => new Twalk(t, 1000, 1001, ["cs"]));
        uint now = (uint)clock.Now.ToUnixTimeSeconds();

        Stat dir = new Rstat(await RpcAsync(t => new Tstat(t, 1000))).Stat;
        Assert.Equal((".", (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101, "bob", "bob", "bob", now, now, 0ul), (dir.Name, dir.Mode, dir.Uid, dir.Gid, dir.Muid, dir.Atime, dir.Mtime, dir.Length));
        Stat file = new Rstat(await RpcAsync(t => new Tstat(t, 1001))).Stat;
        Assert.Equal(("cs", 0b110_110_110u, QidType.QTFILE, 1ul), (file.Name, file.Mode, file.Qid.Type, file.Qid.Path));

        await RpcAsync(t => new Topen(t, 1000, NinePConstants.OREAD));
        byte[] entries = new Rread(await RpcAsync(t => new Tread(t, 1000, 0, 8192))).Data.ToArray();
        int at = 0;
        var entry = new Stat(entries, ref at);
        Assert.Equal(entries.Length, at);
        Assert.Equal(("cs", 0b110_110_110u, QidType.QTFILE, 1ul, "bob", now), (entry.Name, entry.Mode, entry.Qid.Type, entry.Qid.Path, entry.Uid, entry.Mtime));
        Assert.Empty(new Rread(await RpcAsync(t => new Tread(t, 1000, (ulong)entries.Length, 8192))).Data.ToArray());
        Assert.Equal("cs: bad read offset", Error(await RpcAsync(t => new Tread(t, 1000, ulong.MaxValue, 8192))));
        Assert.Equal("cs: can't write directory", Error(await RpcAsync(t => new Twrite(t, 1000, 0, "tcp!*!1"u8.ToArray()))));
    }

    [Fact]
    public async Task NothingCanBeMadeRemovedOrChangedAndTheDirectoryOpensOnlyToRead()
    {
        await StartAsync();
        await RpcAsync(t => new Tattach(t, 1000, NoFid, "bob", string.Empty));
        await RpcAsync(t => new Twalk(t, 1000, 1001, ["cs"]));
        Assert.Equal("cs: permission denied", Error(await RpcAsync(t => new Topen(t, 1000, NinePConstants.OWRITE))));
        Assert.Equal((QidType.QTDIR, 0u), Result(new Ropen(await RpcAsync(t => new Topen(t, 1000, NinePConstants.OREAD)))));
        Assert.Equal((QidType.QTFILE, 0u), Result(new Ropen(await RpcAsync(t => new Topen(t, 1001, NinePConstants.ORDWR)))));
        Assert.Equal("cs: creation permission denied", Error(await RpcAsync(t => new Tcreate(t, 1000, "x", 0b110_110_110, NinePConstants.OREAD))));
        Assert.Equal("cs: remove permission denied", Error(await RpcAsync(t => new Tremove(t, 1001))));
        Stat stat = new Rstat(await RpcAsync(t => new Tstat(t, 1001))).Stat;
        Assert.Equal("cs: wstat permission denied", Error(await RpcAsync(t => new Twstat(t, 1001, stat))));
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 1001)))[4]);
    }

    [Fact]
    public async Task AnAnswerIsReadInPiecesAtItsOffsetsAndLookingOnFindsNoMore()
    {
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(9u, new Rwrite(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!*!564"u8.ToArray()))).Count);
        Assert.Equal("/net", await ReadAsync(cs, 0, 4));
        Assert.Equal("/tcp/clone 564!r", await ReadAsync(cs, 4, 100));
        Assert.Equal(string.Empty, await ReadAsync(cs, 20, 100));

        await RpcAsync(t => new Twrite(t, cs, 0, "net!*!564"u8.ToArray()));
        Assert.Equal("/net/tcp/clone 564!r", await ReadAsync(cs, 0, 100));
        Assert.Equal(string.Empty, await ReadAsync(cs, 20, 100));
    }

    [Fact]
    public async Task TheOwnerTurnsDebuggingAndEachIpVersionsLookupsOnAndOffAndOthersAskForThemAsAddresses()
    {
        await MkdirAsync("#R/sys");
        await MkdirAsync("#R/sys/log");
        await WriteFileAsync("/sys/log/cs", string.Empty);
        host.Names["two.example"] = [IPAddress.Parse("10.9.9.9"), IPAddress.Parse("2001:db8::9")];
        await StartAsync();
        uint cs = await OpenCsAsync("glenda");

        // The address on the network of one of the host's interfaces comes first.
        Assert.Equal(["/net/tcp/clone 2001:db8::9!1", "/net/tcp/clone 10.9.9.9!1"], await QueryAsync(cs, "tcp!two.example!1"));

        await RpcAsync(t => new Twrite(t, cs, 0, "debug"u8.ToArray()));
        await RpcAsync(t => new Twrite(t, cs, 0, "ipv4"u8.ToArray()));
        Assert.Equal(["/net/tcp/clone 2001:db8::9!1"], await QueryAsync(cs, "tcp!two.example!1"));

        // The slave logs its death once it has replied, so the log is waited on before going on.
        await WaitForAsync(async () => (await ReadFileAsync("/sys/log/cs")).Contains("slave death", StringComparison.Ordinal));
        await RpcAsync(t => new Twrite(t, cs, 0, "ipv6"u8.ToArray()));
        Assert.Equal("cs: no ip address", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!two.example!1"u8.ToArray()))));
        await RpcAsync(t => new Twrite(t, cs, 0, "ipv4"u8.ToArray()));
        await RpcAsync(t => new Twrite(t, cs, 0, "ipv6"u8.ToArray()));
        await RpcAsync(t => new Twrite(t, cs, 0, "debug"u8.ToArray()));
        Assert.Equal(2, (await QueryAsync(cs, "tcp!two.example!1")).Count);

        string log = await ReadFileAsync("/sys/log/cs");
        string[] lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.StartsWith("fogtest Oct 19 12:00:00 ", line, StringComparison.Ordinal));
        Assert.Equal(
            ["debug 1", "ipv4lookups 0", "write tcp!two.example!1", "slave", "slave death", "ipv6lookups 0", "write tcp!two.example!1", "ipv4lookups 1", "ipv6lookups 1", "debug 0"],
            lines.Select(line => line["fogtest Oct 19 12:00:00 ".Length..]).Select(m => m.StartsWith("slave", StringComparison.Ordinal) ? string.Join(' ', m.Split(' ').Where(w => !long.TryParse(w, out _))) : m));

        // What the owner changes is told on the console as well.
        Assert.Equal(
            string.Concat(((string[])["debug 1", "ipv4lookups 0", "ipv6lookups 0", "ipv4lookups 1", "ipv6lookups 1", "debug 0"]).Select(l => $"fogtest Oct 19 12:00:00 {l}\n")),
            console.Text);

        uint none = await OpenCsAsync("none", 2100);
        Assert.Equal("cs: can't translate service", Error(await RpcAsync(t => new Twrite(t, none, 0, "ipv4"u8.ToArray()))));
        uint other = await OpenCsAsync("bob", 3000);
        Assert.Equal("cs: can't translate service", Error(await RpcAsync(t => new Twrite(t, other, 0, "debug"u8.ToArray()))));
    }

    [Fact]
    public async Task TheNextNetworksAnswersAreLookedUpOnlyWhenTheReadsReachThem()
    {
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        await MkdirAsync("/tmp/nonet");
        await StartAsync("-x", "/tmp/nonet");
        uint cs = await OpenCsAsync("glenda", srv: "/srv/cs_tmp_nonet");
        await RpcAsync(t => new Twrite(t, cs, 0, "add il tcp"u8.ToArray()));
        Assert.Equal((byte)MessageTypes.Rwrite, (await RpcAsync(t => new Twrite(t, cs, 0, "net!slow.example!1"u8.ToArray())))[4]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        Assert.Equal("/tmp/nonet/il/clone 10.9.9.9!1!fasttimeout", await ReadAsync(cs, 0, 8192));
        gate.SetResult();
    }

    [Fact]
    public async Task TheOwnerAddsAMountedNetworkToTheDefaultsAndRefreshFindsOneMountedSince()
    {
        await MkdirAsync("/tmp/nonet");
        await StartAsync("-x", "/tmp/nonet");
        uint cs = await OpenCsAsync("glenda", srv: "/srv/cs_tmp_nonet");
        Assert.Equal("cs: ", Error(await RpcAsync(t => new Twrite(t, cs, 0, "net!*!1"u8.ToArray()))));
        await RpcAsync(t => new Twrite(t, cs, 0, "add udp\n"u8.ToArray()));
        Assert.Equal("cs: ", Error(await RpcAsync(t => new Twrite(t, cs, 0, "net!*!1"u8.ToArray()))));
        await RpcAsync(t => new Twrite(t, cs, 0, "add tcp\n"u8.ToArray()));
        Assert.Equal(["/tmp/nonet/tcp/clone 1"], await QueryAsync(cs, "net!*!1"));

        // Each il is added, the one with the fast timeout first, for an address not known to be il's.
        await RpcAsync(t => new Twrite(t, cs, 0, "add  il   tcp"u8.ToArray()));
        Assert.Equal(["/tmp/nonet/tcp/clone 1", "/tmp/nonet/il/clone 1"], await QueryAsync(cs, "net!*!1"));
        Assert.Equal(["/tmp/nonet/tcp/clone 192.168.1.10!1", "/tmp/nonet/il/clone 192.168.1.10!1!fasttimeout", "/tmp/nonet/il/clone 192.168.1.10!1"], await QueryAsync(cs, "net!fogtest!1"));
        Assert.Equal(["/tmp/nonet/tcp/clone 10.2.2.2!1", "/tmp/nonet/il/clone 10.2.2.2!1"], await QueryAsync(cs, "net!ilhost!1"));
        Assert.Equal(["/tmp/nonet/il/clone 10.2.2.2!1"], await QueryAsync(cs, "il!ilhost!1"));

        // The directories are there before cs mounts, but not their clones.
        await MkdirAsync("/tmp/other");
        await MkdirAsync("/tmp/other/udp");
        await MkdirAsync("/tmp/other/tcp");
        await MkdirAsync("/tmp/other/il");
        await StartAsync("-x", "/tmp/other");
        uint fresh = await OpenCsAsync("glenda", srv: "/srv/cs_tmp_other");
        Assert.Equal("cs: ", Error(await RpcAsync(t => new Twrite(t, fresh, 0, "net!*!1"u8.ToArray()))));
        await WriteFileAsync("/tmp/other/udp/clone", string.Empty);
        await WriteFileAsync("/tmp/other/tcp/clone", string.Empty);
        kernel.Sysname = "changed";
        await RpcAsync(t => new Twrite(t, fresh, 0, "refresh"u8.ToArray()));
        await WaitForAsync(async () => (await RpcAsync(t => new Twrite(t, fresh, 0, "net!*!1"u8.ToArray())))[4] == (byte)MessageTypes.Rwrite);
        Assert.Equal(["/tmp/other/tcp/clone 1"], await QueryAsync(fresh, "net!*!1"));

        // The system's name, once known, is not looked for again.
        Assert.Equal("changed", kernel.Sysname);

        // A refresh after one that has ended finds what was mounted since.
        await WriteFileAsync("/tmp/other/il/clone", string.Empty);
        await RpcAsync(t => new Twrite(t, fresh, 0, "refresh"u8.ToArray()));
        await WaitForAsync(async () => (await QueryAsync(fresh, "net!*!1")).Count == 2);
        Assert.Equal(["/tmp/other/tcp/clone 1", "/tmp/other/il/clone 1"], await QueryAsync(fresh, "net!*!1"));
    }

    [Fact]
    public async Task TheSystemNameIsTheEnvironmentsUnlessAnAddressThenTheDatabasesForAnInterfaceThenTheAddress()
    {
        await StartAsync("-n");
        Assert.Equal("fogtest", await ReadFileAsync("/dev/sysname"));
        Assert.False(await ExistsAsync("/srv/cs"));

        await WriteFileAsync("/env/sysname", "10.0.0.1");
        kernel.Sysname = string.Empty;
        await StartAsync("-n");
        Assert.Equal("fogtest", await ReadFileAsync("/dev/sysname"));

        await WriteFileAsync("/env/sysname", "fromenv");
        await StartAsync("-n");
        Assert.Equal("fromenv", await ReadFileAsync("/dev/sysname"));

        // With no sys for any interface's address, it is the first interface's first address.
        await init.RemoveAsync("/env/sysname");
        await WriteFileAsync("/tmp/ndb", "ip=192.168.1.10 dom=x.example\n");
        await StartAsync("-n");
        Assert.Equal("127.0.0.1", await ReadFileAsync("/dev/sysname"));

        host.Ifcs.Clear();
        kernel.Sysname = "kept";
        await StartAsync("-n");
        Assert.Equal("kept", await ReadFileAsync("/dev/sysname"));
    }

    [Fact]
    public async Task AQueryWhileOneIsInProgressIsRefusedAndAFlushedAnswerIsNeverSent()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        ushort slow = ++tag;
        await SendAsync(new Twrite(slow, cs, 0, "tcp!slow.example!1"u8.ToArray()));
        Assert.Equal("cs: query already in progress", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!*!1"u8.ToArray()))));
        uint other = await OpenCsAsync("bob", 3000);
        Assert.Equal(["/net/tcp/clone 1"], await QueryAsync(other, "tcp!*!1"));
        Assert.Equal((byte)MessageTypes.Rflush, (await RpcAsync(t => new Tflush(t, slow)))[4]);
        gate.SetResult();
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, other)))[4]);
        await WaitForAsync(async () => (await RpcAsync(t => new Twrite(t, cs, 0, "tcp!*!1"u8.ToArray())))[4] == (byte)MessageTypes.Rwrite);
    }

    [Fact]
    public async Task MoreSlavesThanCsAllowsAreTooMuchActivity()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        await StartAsync();
        var slow = new List<ushort>();
        for (uint fid = 0; fid < 199; fid++)
        {
            uint cs = await OpenCsAsync("bob", 5000 + (fid * 2));
            slow.Add(++tag);
            await SendAsync(new Twrite(tag, cs, 0, "tcp!slow.example!1"u8.ToArray()));
        }

        uint last = await OpenCsAsync("bob", 4000);
        await WaitForAsync(async () => Error(await RpcAsync(t => new Twrite(t, last, 0, "tcp!slow.example!1"u8.ToArray()))) == "cs: too much activity");

        // Each refusal gives its slave back, so once the others are done there is room again.
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal("cs: too much activity", Error(await RpcAsync(t => new Twrite(t, last, 0, "tcp!slow.example!1"u8.ToArray()))));
        }

        gate.SetResult();
        var answered = new HashSet<ushort>();
        while (answered.Count < slow.Count)
        {
            byte[] reply = await ReceiveAsync();
            Assert.Equal((byte)MessageTypes.Rwrite, reply[4]);
            answered.Add(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5)));
        }

        Assert.Equal(slow.ToHashSet(), answered);
        Assert.Equal(["/net/tcp/clone 10.9.9.9!1"], await QueryAsync(last, "tcp!slow.example!1"));
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, "cs: can't translate address: dns: name does not exist")]
    [InlineData(SocketError.NoData, "cs: can't translate address: dns: resource does not exist; negrcode")]
    [InlineData(SocketError.TryAgain, "cs: temporary problem: dns: dns failure")]
    public async Task ADomainNameTheResolverCannotGiveAddressesForSaysWhy(SocketError failure, string error)
    {
        host.Failure = failure;
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(error, Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!x.example!1"u8.ToArray()))));
    }

    [Fact]
    public async Task TheResolversAnswersAreDnssOnesOnceEachWithAReverseLookupByItsPtrName()
    {
        host.Names["empty.example"] = [];
        host.Names["dup.example"] = [IPAddress.Parse("10.9.9.9"), IPAddress.Parse("10.9.9.9")];
        host.Reverse[IPAddress.IPv6Loopback] = "localhost6";
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal("cs: can't translate address: dns: resource does not exist; negrcode", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!empty.example!1"u8.ToArray()))));
        Assert.Equal(["/net/tcp/clone 10.9.9.9!1"], await QueryAsync(cs, "tcp!dup.example!1"));
        Assert.Equal(["ptr=1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.ip6.arpa dom=localhost6"], await QueryAsync(cs, "!ip=::1"));
        Assert.Equal(["ptr=1.0.0.127.in-addr.arpa dom=localhost"], await QueryAsync(cs, "!ip=127.0.0.1"));
        Assert.Equal(["dom=dup.example ip=10.9.9.9"], await QueryAsync(cs, "!dom=dup.example"));
        Assert.Equal("cs: no match", Error(await RpcAsync(t => new Twrite(t, cs, 0, "!dom=dup.example ip=10.0.0.1"u8.ToArray()))));
        Assert.Equal("cs: no match", Error(await RpcAsync(t => new Twrite(t, cs, 0, "!ip=0.0.0.0"u8.ToArray()))));

        host.Reverse[IPAddress.Any] = "zero";
        host.Reverse[IPAddress.IPv6Any] = "zero6";
        Assert.Equal("cs: no match", Error(await RpcAsync(t => new Twrite(t, cs, 0, "!ip=::"u8.ToArray()))));
        Assert.Equal("cs: no match", Error(await RpcAsync(t => new Twrite(t, cs, 0, "!ip=0.0.0.0"u8.ToArray()))));
        Assert.Equal([$"ptr={string.Concat(Enumerable.Repeat("0.", 32))}ip6.arpa dom=zero6"], await QueryAsync(cs, "!ip=1::2::3"));

        host.Thrown = new ArgumentException("bad name");
        Assert.Equal("cs: temporary problem: dns: dns failure", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!x.example!1"u8.ToArray()))));
    }

    [Fact]
    public async Task WithNoAddressOtherThanLoopbackThereIsNoIpAddressToAskDnsFrom()
    {
        host.Ifcs.Clear();
        host.Ifcs.Add(new HostInterface("lo", [new(IPAddress.Loopback, 8), new(IPAddress.IPv6Loopback, 128), new(IPAddress.Parse("fe80::1"), 64)]));
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal("cs: no ip address", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!localhost.example!1"u8.ToArray()))));
        Assert.Equal(["/net/tcp/clone 127.0.0.1!1"], await QueryAsync(cs, "tcp!127.0.0.1!1"));
        Assert.Equal(["/net/tcp/clone ::1!1"], await QueryAsync(cs, "tcp!::1!1"));
    }

    [Fact]
    public async Task AMessageInTwoWritesIsReadWhole()
    {
        await StartAsync();
        byte[] version = Encode(new Tversion(1, 8216, "9P2000"));
        await init.WriteAsync(fd, version.AsMemory(0, 7));
        await init.WriteAsync(fd, version.AsMemory(7));
        Assert.Equal((byte)MessageTypes.Rversion, (await ReceiveAsync())[4]);
    }

    [Fact]
    public async Task OnlyTheNetworksNetLooksThroughAreDefaultsAndEachTakesItsOwnIpVersions()
    {
        await WriteFileAsync("/tmp/ndb", "sys=h2 ip=10.3.3.3 x=il\nsys=h3 ip=10.3.3.4 proto=tcp\nsys=h4 ip=10.3.3.5 proto=il\nssh=sshsvc port=22\n");
        await MkdirAsync("/tmp/all");
        foreach (string net in (string[])["tcp", "il", "udp", "icmp", "icmpv6", "rudp", "ssh", "telco"])
        {
            await MkdirAsync($"/tmp/all/{net}");
            await WriteFileAsync($"/tmp/all/{net}/clone", string.Empty);
        }

        await StartAsync("-x", "/tmp/all");
        uint cs = await OpenCsAsync("bob", srv: "/srv/cs_tmp_all");

        // The il with the fast timeout comes first, as the network table has it.
        Assert.Equal(["/tmp/all/il/clone 1", "/tmp/all/tcp/clone 1"], await QueryAsync(cs, "net!*!1"));
        Assert.Equal(["/tmp/all/il/clone 10.3.3.3!1!fasttimeout", "/tmp/all/tcp/clone 10.3.3.3!1", "/tmp/all/il/clone 10.3.3.3!1"], await QueryAsync(cs, "net!h2!1"));
        Assert.Equal(["/tmp/all/il/clone 10.3.3.4!1!fasttimeout", "/tmp/all/tcp/clone 10.3.3.4!1", "/tmp/all/il/clone 10.3.3.4!1"], await QueryAsync(cs, "net!h3!1"));
        Assert.Equal(["/tmp/all/il/clone 10.3.3.5!1", "/tmp/all/tcp/clone 10.3.3.5!1"], await QueryAsync(cs, "net!h4!1"));
        Assert.Equal(["/tmp/all/icmp/clone 1"], await QueryAsync(cs, "icmp!*!1"));
        Assert.Equal(["/tmp/all/ssh/clone 22"], await QueryAsync(cs, "ssh!*!sshsvc"));
        foreach ((string net, bool v4, bool v6) in (ValueTuple<string, bool, bool>[])[("udp", true, true), ("icmp", true, false), ("icmpv6", false, true), ("rudp", true, false), ("ssh", true, true), ("tcp", true, true)])
        {
            foreach ((string address, bool takes) in (ValueTuple<string, bool>[])[("10.1.2.3", v4), ("::1", v6)])
            {
                byte[] written = await RpcAsync(t => new Twrite(t, cs, 0, Encoding.UTF8.GetBytes($"{net}!{address}!1")));
                if (takes)
                {
                    Assert.Equal($"/tmp/all/{net}/clone {address}!1", await ReadAsync(cs, 0, 8192));
                }
                else
                {
                    Assert.Equal("cs: can't translate address", Error(written));
                }
            }
        }
    }

    [Fact]
    public async Task AddTakesTwelveNamesTheLastRunningToTheEndAndOnlyAWritesLastNewlineIsDropped()
    {
        await MkdirAsync("/tmp/nonet");
        await StartAsync("-x", "/tmp/nonet");
        uint cs = await OpenCsAsync("glenda", srv: "/srv/cs_tmp_nonet");
        await RpcAsync(t => new Twrite(t, cs, 0, "add 1 2 3 4 5 6 7 8 9 10 11 il tcp"u8.ToArray()));
        await RpcAsync(t => new Twrite(t, cs, 0, "add tcp\n\0x"u8.ToArray()));
        Assert.Equal("cs: ", Error(await RpcAsync(t => new Twrite(t, cs, 0, "net!*!1"u8.ToArray()))));
        await RpcAsync(t => new Twrite(t, cs, 0, "add tcp\0x\n"u8.ToArray()));
        Assert.Equal(["/tmp/nonet/tcp/clone 1"], await QueryAsync(cs, "net!*!1"));

        await MkdirAsync("/tmp/nonet2");
        await StartAsync("-x", "/tmp/nonet2");
        cs = await OpenCsAsync("glenda", srv: "/srv/cs_tmp_nonet2");
        await RpcAsync(t => new Twrite(t, cs, 0, "add  1 2 3 4 5 6 7 8 9 10 11  tcp"u8.ToArray()));
        Assert.Equal(["/tmp/nonet2/tcp/clone 1"], await QueryAsync(cs, "net!*!1"));
    }

    [Fact]
    public async Task AnAnswerIsCutToCssReplyAndAServiceToItsServiceAndAMountPointToItsPath()
    {
        // A mount point whose srv name is as long as devsrv takes.
        string mntpt = "/tmp/" + new string('m', 120);
        await MkdirAsync(mntpt);
        await StartAsync("-x", mntpt);
        uint cs = await OpenCsAsync("bob", srv: "/srv/cs" + mntpt.Replace('/', '_'));

        // A whole answer of 256 bytes loses its last; one of 255 is kept.
        string host = new('h', 60), serv = new('s', 60);
        Assert.Equal([$"{mntpt}/zz/clone {host}!{serv[..^1]}"], await QueryAsync(cs, $"zz!{host}!{serv}"));
        Assert.Equal([$"{mntpt}/zz/clone {host}!{serv[..^1]}"], await QueryAsync(cs, $"zz!{host}!{serv[..^1]}"));

        await StartAsync();
        uint net = await OpenCsAsync("bob", 3000, "/srv/cs");
        Assert.Equal([$"/net/tcp/clone 1!{new string('r', 62)}"], await QueryAsync(net, "tcp!*!1!" + new string('r', 63)));
        Assert.Equal([$"/net/tcp/clone 1!{new string('r', 62)}"], await QueryAsync(net, "tcp!*!1!" + new string('r', 62)));
        Assert.Equal([$"/net/telco/clone 555!1!{new string('r', 62)}"], await QueryAsync(net, "telco!555!1!" + new string('r', 63)));
    }

    [Fact]
    public async Task AMountPointIsCutToCssPathAndAServeFileDevsrvCannotNameEndsCs()
    {
        await LogAsync();
        string longest = "/tmp/" + new string('m', 122);
        await RunCsAsync("-x", longest + "x");
        string line = (await ReadFileAsync("/sys/log/cs")).TrimEnd('\n');
        Assert.StartsWith($"fogtest Oct 19 12:00:00 /srv/cs{longest.Replace('/', '_')}: ", line, StringComparison.Ordinal);
        Assert.DoesNotContain(longest.Replace('/', '_') + "x", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestOfCsRequestsLengthOrMoreIsTooLongAndARemainingQueryIsForgottenOnTheNext()
    {
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal("cs: can't translate service", Error(await RpcAsync(t => new Twrite(t, cs, 0, Encoding.UTF8.GetBytes("tcp!*!" + new string('a', 121))))));
        Assert.Equal("cs: request too long", Error(await RpcAsync(t => new Twrite(t, cs, 0, Encoding.UTF8.GetBytes("tcp!*!" + new string('a', 122))))));
        Assert.Equal(["/net/tcp/clone 1!x"], await QueryAsync(cs, "tcp!*!1!x"));
        Assert.Equal(["/net/tcp/clone 1"], await QueryAsync(cs, "tcp!*!1"));
        Assert.Equal(["/net/tcp/clone 1"], await QueryAsync(cs, "net!*!1"));
        Assert.Equal(["/net/tcp/clone 1"], await QueryAsync(cs, "net!*!1"));
        Assert.Equal("cs: no match", Error(await RpcAsync(t => new Twrite(t, cs, 0, "!sys=nobody"u8.ToArray()))));
        Assert.Equal(string.Empty, await ReadAsync(cs, 0, 8192));
        Assert.Equal("cs: ", Error(await RpcAsync(t => new Twrite(t, cs, 0, Array.Empty<byte>()))));
    }

    [Fact]
    public async Task AnEntryGivesAtMostTwentyAnswers()
    {
        await WriteFileAsync("/tmp/ndb", "sys=many\n" + string.Concat(Enumerable.Range(1, 22).Select(i => $"\tip=10.9.0.{i}\n")));
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(20, (await QueryAsync(cs, "tcp!many!1")).Count);
        Assert.Equal(20, (await QueryAsync(cs, "net!many!1")).Count);
        Assert.Equal(["sys=many", .. Enumerable.Range(1, 19).Select(i => $"ip=10.9.0.{i}")], await QueryAsync(cs, "!sys=many"));
    }

    [Fact]
    public async Task CsIsNamedForItsMountPointAndASlaveForTheNameItLooksUpAndASlaveEndsWhenItHasAnswered()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        await StartAsync();
        Assert.Contains("cs [/net]", await ArgsAsync());
        uint cs = await OpenCsAsync("bob");
        await SendAsync(new Twrite(++tag, cs, 0, "tcp!slow.example!1"u8.ToArray()));
        await WaitForAsync(async () => (await ArgsAsync()).Contains("cs [slow.example]"));
        gate.SetResult();
        Assert.Equal((byte)MessageTypes.Rwrite, (await ReceiveAsync())[4]);
        await WaitForAsync(async () => !(await ArgsAsync()).Contains("cs [slow.example]"));
    }

    [Fact]
    public async Task ASlaveAndARefreshAreEachInANoteGroupOfTheirOwn()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        await StartAsync();
        long server = kernel.Processes.Values.Single(p => p.Args == "cs [/net]").Pid;
        string noteid = await ReadFileAsync($"/proc/{server}/noteid");
        uint cs = await OpenCsAsync("bob");
        await SendAsync(new Twrite(++tag, cs, 0, "tcp!slow.example!1"u8.ToArray()));
        await WaitForAsync(() => Task.FromResult(kernel.Processes.Values.Any(p => p.Args == "cs [slow.example]")));
        long slave = kernel.Processes.Values.Single(p => p.Args == "cs [slow.example]").Pid;
        Assert.NotEqual(noteid, await ReadFileAsync($"/proc/{slave}/noteid"));
        gate.SetResult();
        Assert.Equal((byte)MessageTypes.Rwrite, (await ReceiveAsync())[4]);

        await WaitForAsync(() => Task.FromResult(!kernel.Processes.ContainsKey(slave)));
        var before = kernel.Processes.Keys.ToHashSet();
        var interfaces = new ManualResetEventSlim();
        host.InterfacesGate = interfaces;
        uint owner = await OpenCsAsync("glenda", 3000);
        await RpcAsync(t => new Twrite(t, owner, 0, "refresh"u8.ToArray()));
        await WaitForAsync(() => Task.FromResult(kernel.Processes.Keys.Any(pid => !before.Contains(pid))));
        long refresh = kernel.Processes.Keys.Single(pid => !before.Contains(pid));
        Assert.NotEqual(noteid, await ReadFileAsync($"/proc/{refresh}/noteid"));
        interfaces.Set();
    }

    [Fact]
    public async Task AFlushedRequestIsNeverAnsweredButANewerOneStillIs()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        await StartAsync();
        uint a = await OpenCsAsync("bob");
        uint b = await OpenCsAsync("bob", 3000);
        ushort older = ++tag;
        await SendAsync(new Twrite(older, a, 0, "tcp!slow.example!1"u8.ToArray()));
        ushort newer = ++tag;
        await SendAsync(new Twrite(newer, b, 0, "tcp!slow.example!1"u8.ToArray()));
        Assert.Equal((byte)MessageTypes.Rflush, (await RpcAsync(t => new Tflush(t, older)))[4]);
        gate.SetResult();
        byte[] reply = await ReceiveAsync();
        Assert.Equal(((byte)MessageTypes.Rwrite, newer), (reply[4], BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5))));
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, b)))[4]);
    }

    [Fact]
    public async Task ASlaveKilledWhileDnsIsAskedDiesThereAndCsGoesOn()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        await SendAsync(new Twrite(++tag, cs, 0, "tcp!slow.example!1"u8.ToArray()));
        await WaitForAsync(() => Task.FromResult(host.Waiting > 0));
        Process slave = kernel.Processes.Values.Single(p => p.Args == "cs [slow.example]");
        Assert.True(slave.PostNote("kill"));
        await WaitForAsync(() => Task.FromResult(!kernel.Processes.ContainsKey(slave.Pid)));
        uint other = await OpenCsAsync("bob", 3000);
        Assert.Equal(["/net/tcp/clone 1"], await QueryAsync(other, "tcp!*!1"));
        gate.SetResult();
    }

    [Fact]
    public async Task DnsIsAskedForADomainNameFirstAndLastButNotAgainForTheDatabasesDomainOfIt()
    {
        await WriteFileAsync("/tmp/ndb", Database + "dom=gone.example sys=gone\n");
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        int before = host.Resolves;
        Assert.StartsWith("cs: can't translate address", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!gone.example!1"u8.ToArray()))), StringComparison.Ordinal);
        Assert.Equal(4, host.Resolves - before);
    }

    [Fact]
    public async Task ANullAddressDnsGivesUpOnLeavesTheQueryToTheDatabase()
    {
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal("cs: no match", Error(await RpcAsync(t => new Twrite(t, cs, 0, "!ip=0.0.0.0"u8.ToArray()))));
        Assert.Equal(["sys=fogtest ip=192.168.1.10"], await QueryAsync(cs, "!ip=192.168.1.10"));
    }

    [Fact]
    public async Task CsgetvalueGivesTheValueOfTheAttributeAskedForInWhatCsGives()
    {
        await StartAsync();
        Assert.Equal("192.168.1.10", await NinePSharp.Fog.Commands.Ndb.CsGetValueAsync(init, "/net", "sys", "fogtest", "ip"));
        Assert.Null(await NinePSharp.Fog.Commands.Ndb.CsGetValueAsync(init, "/net", "sys", "nobody", "ip"));
        Assert.Null(await NinePSharp.Fog.Commands.Ndb.CsGetValueAsync(init, "/nonet", "sys", "fogtest", "ip"));
    }

    [Fact]
    public async Task AnAddressOnTheNetworkOfAnInterfaceComesFirst()
    {
        await WriteFileAsync("/tmp/ndb", Database + "sys=low ip=10.0.0.1 ip=0.0.0.1\n");
        host.Ifcs.Add(new HostInterface("ether1", [new(IPAddress.Parse("0.0.0.9"), 8)]));
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(["/net/tcp/clone 0.0.0.1!1", "/net/tcp/clone 10.0.0.1!1"], await QueryAsync(cs, "tcp!low!1"));
    }

    [Fact]
    public async Task CsEndsQuietlyOnceItsLastClientHasGone()
    {
        // Under RFNOMNT its mount is refused before it is attached, so only /srv and whoever opens it
        // hold the other end of its pipe; with descriptors of its own, as rc starts a command, it
        // leaves none of its own behind.
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg | RforkFlags.Nomnt, child => child.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb"]));
        await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        long server = kernel.Processes.Values.Single(p => p.Args == "cs [/net]").Pid;
        string logged = console.Text;
        fd = await init.OpenAsync("/srv/cs", NinePConstants.ORDWR);
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(["/net/tcp/clone 1"], await QueryAsync(cs, "tcp!*!1"));
        await init.RemoveAsync("/srv/cs");
        await init.CloseAsync(fd);
        fd = -1;
        await WaitForAsync(() => Task.FromResult(!kernel.Processes.ContainsKey(server)));
        Assert.Contains(" mount failed: ", logged, StringComparison.Ordinal);
        Assert.Equal(logged, console.Text);
    }

    [Fact]
    public async Task CsEndsLoggingWhyWhenItCannotMakeItsPipe()
    {
        init.Fork(RforkFlags.Proc, async child =>
        {
            int last = -1;
            try
            {
                for (; ;)
                {
                    last = await child.OpenAsync("/dev/null", NinePConstants.OREAD);
                }
            }
            catch (SyscallException)
            {
                // The table is full; one descriptor is given back below.
            }

            await child.CloseAsync(last);
            await child.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb"]);
        });
        await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // The last descriptor goes to the console, so neither the system's name nor the time can be read.
        Assert.Equal(" Jan  1 00:00:00 pipe failed: no free file descriptors\n", console.Text);
    }

    [Fact]
    public async Task AFlushIsOfTheNewestRequestStillInProgressWithItsTag()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        host.Names["fast.example"] = [IPAddress.Parse("10.8.8.8")];
        await StartAsync();
        uint a = await OpenCsAsync("bob");
        uint b = await OpenCsAsync("bob", 3000);
        ushort reused = ++tag;
        await SendAsync(new Twrite(reused, a, 0, "tcp!slow.example!1"u8.ToArray()));
        await WaitForAsync(() => Task.FromResult(host.Waiting > 0));
        host.Gate = null;

        // The same tag again, answered by a slave of its own while the first is still waiting.
        await SendAsync(new Twrite(reused, b, 0, "tcp!fast.example!1"u8.ToArray()));
        byte[] reply = await ReceiveAsync();
        Assert.Equal(((byte)MessageTypes.Rwrite, reused), (reply[4], BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5))));
        Assert.Equal((byte)MessageTypes.Rflush, (await RpcAsync(t => new Tflush(t, reused)))[4]);
        gate.SetResult();
        await WaitForAsync(async () => !(await ArgsAsync()).Contains("cs [slow.example]"));
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, b)))[4]);
    }

    [Fact]
    public async Task AFidClunkedAndAttachedAgainIsTheNewAttachersAndWalkErrorsAreTheFirstMet()
    {
        await StartAsync();
        await RpcAsync(t => new Tattach(t, 1000, NoFid, "bob", string.Empty));
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 1000)))[4]);
        await RpcAsync(t => new Tattach(t, 1000, NoFid, "alice", string.Empty));
        Assert.Equal("alice", new Rstat(await RpcAsync(t => new Tstat(t, 1000))).Stat.Uid);
        Assert.Equal("cs: not a directory", Error(await RpcAsync(t => new Twalk(t, 1000, 1001, ["cs", "nothing"]))));
        Assert.Equal("cs: file does not exist", Error(await RpcAsync(t => new Twalk(t, 1000, 1001, ["nothing", "cs", "x"]))));
        Assert.Equal("cs: file does not exist", Error(await RpcAsync(t => new Twalk(t, 1000, 1000, ["..", "nothing"]))));
        Assert.Equal((QidType.QTDIR, 0ul), (new Rstat(await RpcAsync(t => new Tstat(t, 1000))).Stat.Qid.Type, new Rstat(await RpcAsync(t => new Tstat(t, 1000))).Stat.Qid.Path));
    }

    [Theory]
    [InlineData(new byte[] { 7, 0, 0, 0, 200, 1, 0 }, "format error 7 0 0 0 c8\n")]
    [InlineData(new byte[] { 5, 0, 0, 0, 100 }, "format error 5 0 0 0 64\n")]
    [InlineData(new byte[] { 4, 0, 0, 0 }, "mount read: bad length in 9P2000 message header\n")]
    [InlineData(new byte[] { 0x19, 0x20, 0, 0, 100, 1, 0 }, "mount read: bad length in 9P2000 message header\n")]
    [InlineData(new byte[] { 7, 0 }, "")]
    [InlineData(new byte[] { 19, 0, 0, 0, 100, 1, 0 }, "")]
    public async Task CsEndsAtAMessageItCannotReadOrTheEndOfOneLoggingWhatItCannotRead(byte[] message, string logged)
    {
        await LogAsync();
        await StartAsync();
        await init.WriteAsync(fd, message);

        // A message cut short ends where the pipe reads empty.
        await init.WriteAsync(fd, Array.Empty<byte>());
        Assert.Empty(await ReceiveAsync());
        string expected = string.Concat(logged.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => $"fogtest Oct 19 12:00:00 {l}\n"));
        Assert.Equal(expected, await ReadFileAsync("/sys/log/cs"));
        Assert.Equal(expected, console.Text);
    }

    [Fact]
    public async Task AMountPointNotRootedIsOneUnderNet()
    {
        await StartAsync("-x", ".alt");
        uint cs = await OpenCsAsync("bob", srv: "/srv/cs_net.alt");
        Assert.Equal(["/net.alt/tcp/clone 1"], await QueryAsync(cs, "tcp!*!1"));
    }

    [Fact]
    public async Task DebuggingFromTheStartLogsTheSystemNameOrThatThereIsNone()
    {
        await LogAsync();
        await StartAsync("-d", "-n");
        host.Ifcs.Clear();
        kernel.Sysname = string.Empty;
        await StartAsync("-d", "-n");
        Assert.Equal("fogtest Oct 19 12:00:00 mysysname fogtest\n Oct 19 12:00:00 mysysname ???\n", await ReadFileAsync("/sys/log/cs"));
        Assert.Equal(string.Empty, console.Text);
    }

    [Fact]
    public async Task WithoutALogFileEverythingLoggedIsOnTheConsole()
    {
        await StartAsync("-d", "-n");
        Assert.Equal("fogtest Oct 19 12:00:00 mysysname fogtest\n", console.Text);
    }

    [Fact]
    public async Task WithoutSysnameTheLogsLinesHaveNoSystemName()
    {
        await LogAsync();
        await MkdirAsync("/tmp/dev");
        await WriteFileAsync("/tmp/dev/bintime", string.Empty);
        await init.BindAsync("#c/bintime", "/tmp/dev/bintime", NinePSharp.Namespaces.MountFlags.Replace);
        await init.BindAsync("/tmp/dev", "/dev", NinePSharp.Namespaces.MountFlags.Replace);
        await StartAsync("-d", "-n");
        Assert.Equal(" Oct 19 12:00:00 mysysname fogtest\n", await ReadFileAsync("/sys/log/cs"));
    }

    [Fact]
    public async Task CsEndsLoggingWhyWhenItCannotOpenTheDatabaseOrMount()
    {
        await LogAsync();
        await RunCsAsync("-f", "/nonexistent");
        Assert.False(await ExistsAsync("/srv/cs"));
        await RunCsAsync("-x", "/nonexistent");
        string[] log = (await ReadFileAsync("/sys/log/cs")).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // The first cs ends before it has named the system.
        Assert.Equal(" Oct 19 12:00:00 can't open network database: file does not exist: '/nonexistent'", log[0]);
        Assert.StartsWith("fogtest Oct 19 12:00:00 mount failed: ", log[1], StringComparison.Ordinal);
        Assert.Equal(2, log.Length);
    }

    [Fact]
    public async Task CsIsInANoteGroupOfItsOwn()
    {
        long group = init.Fork(RforkFlags.Proc | RforkFlags.Noteg, async c =>
        {
            c.Fork(RforkFlags.Proc, cc => cc.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb"]));
            await c.WaitAsync();
            await c.SleepAsync(60_000);
        });
        await WaitForAsync(() => ExistsAsync("/srv/cs"));
        int notepg = await init.OpenAsync($"/proc/{group}/notepg", NinePConstants.OWRITE);
        await init.WriteAsync(notepg, "kill"u8.ToArray());
        await init.CloseAsync(notepg);
        Assert.Equal(group, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Pid);
        fd = await init.OpenAsync("/srv/cs", NinePConstants.ORDWR);
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(["/net/tcp/clone 1"], await QueryAsync(cs, "tcp!*!1"));
    }

    [Fact]
    public async Task TheNetworksOwnDatabaseIsSearchedFirstUnlessTheDatabaseNamesIt()
    {
        await MkdirAsync("/tmp/xnet");
        await WriteFileAsync("/tmp/xnet/ndb", "sys=n ip=1.1.1.1\n");
        await WriteFileAsync("/tmp/ndb", "sys=n ip=2.2.2.2\n");
        await StartAsync("-x", "/tmp/xnet");
        uint cs = await OpenCsAsync("bob", srv: "/srv/cs_tmp_xnet");
        Assert.Equal(["sys=n ip=1.1.1.1"], await QueryAsync(cs, "!sys=n"));

        await MkdirAsync("/tmp/ynet");
        await WriteFileAsync("/tmp/ynet/ndb", "sys=n ip=1.1.1.1\n");
        await WriteFileAsync("/tmp/ndb", "database=\n\tfile=/tmp/ndb\n\tfile=/tmp/ynet/ndb\nsys=n ip=2.2.2.2\n");
        await StartAsync("-x", "/tmp/ynet");
        cs = await OpenCsAsync("bob", srv: "/srv/cs_tmp_ynet");
        Assert.Equal(["sys=n ip=2.2.2.2"], await QueryAsync(cs, "!sys=n"));
        Assert.Equal(["/tmp/ynet/tcp/clone 2.2.2.2!1", "/tmp/ynet/tcp/clone 1.1.1.1!1"], await QueryAsync(cs, "tcp!n!1"));
    }

    [Fact]
    public async Task TheInterfacesAreReadAgainWhenTheOwnerAddsANetwork()
    {
        host.Ifcs.Clear();
        host.Ifcs.Add(new HostInterface("lo", [new(IPAddress.Loopback, 8)]));
        await StartAsync();
        uint cs = await OpenCsAsync("glenda");
        Assert.Equal("cs: can't translate address", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!::1!1"u8.ToArray()))));
        host.Ifcs.Add(new HostInterface("lo6", [new(IPAddress.IPv6Loopback, 128)]));
        await RpcAsync(t => new Twrite(t, cs, 0, "add tcp"u8.ToArray()));
        Assert.Equal(["/net/tcp/clone ::1!1"], await QueryAsync(cs, "tcp!::1!1"));
    }

    [Fact]
    public async Task NoAddressIsNoVersionAndEveryV4AddressNotLoopbackIsOne()
    {
        host.Ifcs.Clear();
        host.Ifcs.Add(new HostInterface("lo", [new(IPAddress.Loopback, 8), new(IPAddress.IPv6Any, 0)]));
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal("cs: no ip address", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!x.example!1"u8.ToArray()))));

        host.Ifcs.Clear();
        host.Ifcs.Add(new HostInterface("ether0", [new(IPAddress.Parse("192.168.1.10"), 24)]));
        host.Ifcs.Add(new HostInterface("ether1", [new(IPAddress.Parse("10.0.0.5"), 8)]));
        host.Names["v4.example"] = [IPAddress.Parse("10.9.9.9")];
        await RpcAsync(t => new Tclunk(t, cs));
        await StartAsync();
        fd = await init.OpenAsync("/srv/cs", NinePConstants.ORDWR);
        cs = await OpenCsAsync("bob", 3000);
        Assert.Equal(["/net/tcp/clone 10.9.9.9!1"], await QueryAsync(cs, "tcp!v4.example!1"));
    }

    [Fact]
    public async Task HostsComeFromAnInterfacesAttributeADatabaseDomainOrNoneAndOnlyAddressesAreGiven()
    {
        await WriteFileAsync(
            "/tmp/ndb",
            "ipnet=lan ip=192.168.1.0 ipmask=255.255.255.0 fs=fsname\nsys=fsname ip=192.168.1.20\nsys=aliased dom=real.example\nsys=multi ip=10.1.1.1 auth=10.2.2.2\nsys=r ip=10.4.4.4 auth=192.168.1.99 ip=10.4.4.5\nsys=noip dom=none.example\n");
        host.Names["real.example"] = [IPAddress.Parse("10.9.9.9")];
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(["/net/tcp/clone 192.168.1.20!1"], await QueryAsync(cs, "tcp!$fs!1"));
        Assert.Equal(["/net/tcp/clone 10.9.9.9!1"], await QueryAsync(cs, "tcp!aliased!1"));
        Assert.Equal(["/net/tcp/clone 1"], await QueryAsync(cs, "tcp!::!1"));
        Assert.Equal(["/net/tcp/clone 10.1.1.1!1"], await QueryAsync(cs, "tcp!multi!1"));
        Assert.Equal(["/net/tcp/clone 10.4.4.4!1", "/net/tcp/clone 10.4.4.5!1"], await QueryAsync(cs, "tcp!r!1"));
        Assert.Equal("cs: can't translate address: dns: name does not exist", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!noip!1"u8.ToArray()))));
    }

    [Fact]
    public async Task TheLastAddressQueryThatGaveNoneSaysWhy()
    {
        host.Names["half.example"] = [];
        host.Failure = SocketError.TryAgain;
        host.FailingFamily = AddressFamily.InterNetworkV6;
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal("cs: temporary problem: dns: dns failure", Error(await RpcAsync(t => new Twrite(t, cs, 0, "tcp!half.example!1"u8.ToArray()))));
    }

    [Fact]
    public async Task EveryV6AddressNotLoopbackOrLinkLocalLetsDnsBeAskedForV6()
    {
        host.Ifcs.Clear();
        host.Ifcs.Add(new HostInterface("ether0", [new(IPAddress.IPv6Loopback, 128), new(IPAddress.Parse("2001:db8::10"), 64)]));
        host.Ifcs.Add(new HostInterface("ether1", [new(IPAddress.Parse("2001:db9::10"), 64)]));
        host.Names["v6.example"] = [IPAddress.Parse("2001:db8::99")];
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(["/net/tcp/clone 2001:db8::99!1"], await QueryAsync(cs, "tcp!v6.example!1"));
    }

    [Fact]
    public async Task DnsHasTheFirstWordOnADomainNameAndIpinfoWithNoAddressIsOfTheInterfaces()
    {
        await WriteFileAsync("/tmp/ndb", Database + "dom=both.example ip=10.1.1.1\n=x sys=blank\n");
        host.Names["both.example"] = [IPAddress.Parse("10.9.9.9")];
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(["/net/tcp/clone 10.9.9.9!1"], await QueryAsync(cs, "tcp!both.example!1"));
        Assert.Equal(["sys=fogtest"], await QueryAsync(cs, "!ipinfo sys"));
        Assert.Equal("cs: no match", Error(await RpcAsync(t => new Twrite(t, cs, 0, "!ipinfo =x sys"u8.ToArray()))));
    }

    [Fact]
    public async Task ADatabaseMayStartWithAnEmptyLine()
    {
        await WriteFileAsync("/tmp/ndb", "\n" + Database);
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        Assert.Equal(["sys=fogtest ip=192.168.1.10"], await QueryAsync(cs, "!sys=fogtest"));
    }

    [Fact]
    public async Task NoneMayNotControlCsEvenWhenCsRunsAsNone()
    {
        init.Fork(RforkFlags.Proc, async child =>
        {
            int user = await child.OpenAsync("/dev/user", NinePConstants.OWRITE);
            await child.WriteAsync(user, "none"u8.ToArray());
            await child.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb"]);
        });
        await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fd = await init.OpenAsync("/srv/cs", NinePConstants.ORDWR);
        uint cs = await OpenCsAsync("none");
        Assert.Equal("cs: can't translate service", Error(await RpcAsync(t => new Twrite(t, cs, 0, "ipv4"u8.ToArray()))));
    }

    [Fact]
    public async Task ADatabaseFileRemovedIsStillReadAsItWas()
    {
        await StartAsync();
        uint cs = await OpenCsAsync("bob");
        await init.RemoveAsync("/tmp/ndb");
        Assert.Equal(["sys=fogtest ip=192.168.1.10"], await QueryAsync(cs, "!sys=fogtest"));
    }

    [Fact]
    public async Task TheNetworksOwnDatabaseKeepsTheFilesItsDatabaseEntryNames()
    {
        await MkdirAsync("/tmp/znet");
        await WriteFileAsync("/tmp/znet/ndb", "database=\n\tfile=/tmp/znet/ndb\n\tfile=/tmp/znet/more\n");
        await WriteFileAsync("/tmp/znet/more", "sys=m2 ip=3.3.3.3\n");
        await StartAsync("-x", "/tmp/znet");
        uint cs = await OpenCsAsync("bob", srv: "/srv/cs_tmp_znet");
        Assert.Equal(["sys=m2 ip=3.3.3.3"], await QueryAsync(cs, "!sys=m2"));
        Assert.Equal(["sys=fogtest ip=192.168.1.10"], await QueryAsync(cs, "!sys=fogtest"));
    }

    [Fact]
    public async Task EachAttachAndCloneHasTheNextVersionAndAMessageOfTheWholeMsizeIsRead()
    {
        await StartAsync();
        uint first = new Rattach(await RpcAsync(t => new Tattach(t, 1000, NoFid, "bob", string.Empty))).Qid.Version;
        Assert.Equal(first + 1, new Rattach(await RpcAsync(t => new Tattach(t, 1001, NoFid, "bob", string.Empty))).Qid.Version);
        Assert.Equal(first + 2, Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1000, 1002, ["cs"]))).Wqid).Version);
        await RpcAsync(t => new Topen(t, 1000, NinePConstants.OREAD));
        byte[] entries = new Rread(await RpcAsync(t => new Tread(t, 1000, 0, 8192))).Data.ToArray();
        int at = 0;
        Assert.Equal(first + 3, new Stat(entries, ref at).Qid.Version);

        await RpcAsync(t => new Topen(t, 1002, NinePConstants.ORDWR));
        Assert.Equal("cs: request too long", Error(await RpcAsync(t => new Twrite(t, 1002, 0, new byte[8216 - 23]))));
    }

    [Fact]
    public async Task ACsStartedAgainTakesThePlaceOfTheOneBefore()
    {
        await WriteFileAsync("/tmp/ndba", "tcp=svc port=1111\n");
        await WriteFileAsync("/tmp/ndbb", "tcp=svc port=2222\n");
        await StartAsync("-f", "/tmp/ndba");
        await StartAsync("-f", "/tmp/ndbb");
        int net = await init.OpenAsync("/net/cs", NinePConstants.ORDWR);
        await init.WriteAsync(net, "tcp!*!svc"u8.ToArray());
        await init.SeekAsync(net, 0, 0);
        Assert.Equal("/net/tcp/clone 2222", Encoding.UTF8.GetString((await init.ReadAsync(net, 8192)).Span));
    }

    [Fact]
    public async Task CsKeepsOnlyTheDescriptorsItServesWith()
    {
        await LogAsync();
        host.Names["two.example"] = [IPAddress.Parse("10.9.9.9")];
        await WriteFileAsync("/env/sysname", "fromenv");
        await StartAsync("-d");
        uint cs = await OpenCsAsync("glenda");
        Assert.Equal(["/net/tcp/clone 10.9.9.9!1"], await QueryAsync(cs, "tcp!two.example!1"));
        Assert.Equal(["sys=fogtest ip=192.168.1.10"], await QueryAsync(cs, "!sys=fogtest"));
        await RpcAsync(t => new Twrite(t, cs, 0, "add tcp"u8.ToArray()));
        await RpcAsync(t => new Twrite(t, cs, 0, "refresh"u8.ToArray()));
        await RpcAsync(t => new Twrite(t, cs, 0, "debug"u8.ToArray()));
        await RpcAsync(t => new Twrite(t, cs, 0, "debug"u8.ToArray()));
        await RpcAsync(t => new Tattach(t, 9000, NoFid, "bob", string.Empty));
        await RpcAsync(t => new Tstat(t, 9000));
        await RpcAsync(t => new Topen(t, 9000, NinePConstants.OREAD));
        await RpcAsync(t => new Tread(t, 9000, 0, 8192));
        await Task.Delay(200);
        long server = kernel.Processes.Values.Single(p => p.Args == "cs [/net]").Pid;

        // Its slaves and its refresh share its descriptors, and what it opened before it forked was copied.
        string fds = Regex.Replace(await ReadFileAsync($"/proc/{server}/fd"), @"\)\s+(\d+)\s+\d+ ", ") $1 _ ");
        Assert.Equal("/\n  0 rw |    1 (0000000000000000 0 00) 0 _ #|/data\n  2 w  s    0 (0000000000000004 0 00) 0 _ /srv/cs\n", fds);
    }

    private static byte[] Encode(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }

    private static string Error(byte[] reply)
    {
        Assert.Equal((byte)MessageTypes.Rerror, reply[4]);
        return new Rerror(reply).Ename;
    }

    private static (QidType Type, uint Iounit) Result(Ropen open) => (open.Qid.Type, open.Iounit);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("the condition never held");
    }

    // cs runs as a program does, its parent mounting it and exiting; then the pipe it posts is opened.
    private async Task StartAsync(params string[] arguments)
    {
        await RunCsAsync(arguments);
        if (fd < 0 && !arguments.Contains("-n") && !arguments.Contains("-x"))
        {
            fd = await init.OpenAsync("/srv/cs", NinePConstants.ORDWR);
        }
    }

    private async Task RunCsAsync(params string[] arguments)
    {
        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb", .. arguments]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
    }

    // A fid of cs, attached as the user and opened for reading and writing, on the pipe of the srv given.
    private async Task<uint> OpenCsAsync(string user, uint fid = 2000, string? srv = null)
    {
        if (srv is not null)
        {
            if (fd >= 0)
            {
                await init.CloseAsync(fd);
            }

            fd = await init.OpenAsync(srv, NinePConstants.ORDWR);
        }

        await RpcAsync(t => new Tattach(t, fid, NoFid, user, string.Empty));
        await RpcAsync(t => new Twalk(t, fid, fid + 1, ["cs"]));
        await RpcAsync(t => new Topen(t, fid + 1, NinePConstants.ORDWR));
        return fid + 1;
    }

    private async Task<List<string>> QueryAsync(uint cs, string query)
    {
        byte[] written = await RpcAsync(t => new Twrite(t, cs, 0, Encoding.UTF8.GetBytes(query)));
        Assert.Equal((byte)MessageTypes.Rwrite, written[4]);
        var answers = new List<string>();
        for (ulong offset = 0; ;)
        {
            string answer = await ReadAsync(cs, offset, 8192);
            if (answer.Length == 0)
            {
                return answers;
            }

            answers.Add(answer);
            offset += (ulong)Encoding.UTF8.GetByteCount(answer);
        }
    }

    private async Task<string> ReadAsync(uint cs, ulong offset, uint count)
        => Encoding.UTF8.GetString(new Rread(await RpcAsync(t => new Tread(t, cs, offset, count))).Data.Span);

    private async Task<byte[]> RpcAsync(Func<ushort, ISerializable> request)
    {
        ushort t = ++tag;
        await SendAsync(request(t));
        byte[] reply = await ReceiveAsync();
        Assert.Equal(t, BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5)));
        return reply;
    }

    private async Task SendAsync(ISerializable message) => await init.WriteAsync(fd, Encode(message));

    private async Task<byte[]> ReceiveAsync() => (await init.ReadAsync(fd, 8216).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ToArray();

    // /sys/log/cs, made in the store's root, which / is a union with.
    private async Task LogAsync()
    {
        await MkdirAsync("#R/sys");
        await MkdirAsync("#R/sys/log");
        await WriteFileAsync("/sys/log/cs", string.Empty);
    }

    // What each process's args read.
    private async Task<List<string>> ArgsAsync()
    {
        var args = new List<string>();
        foreach (long pid in kernel.Processes.Keys)
        {
            try
            {
                args.Add(await ReadFileAsync($"/proc/{pid}/args"));
            }
            catch (SyscallException)
            {
                // Gone since.
            }
        }

        return args;
    }

    private async Task<bool> ExistsAsync(string path)
    {
        try
        {
            await init.StatAsync(path);
            return true;
        }
        catch (SyscallException)
        {
            return false;
        }
    }

    private async Task MkdirAsync(string path)
        => await init.CloseAsync(await init.CreateAsync(path, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));

    private async Task WriteFileAsync(string path, string text)
    {
        int f = await init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(f, Encoding.UTF8.GetBytes(text));
        await init.CloseAsync(f);
    }

    private async Task<string> ReadFileAsync(string path)
    {
        int f = await init.OpenAsync(path, NinePConstants.OREAD);
        var all = new List<byte>();
        for (ReadOnlyMemory<byte> block; !(block = await init.ReadAsync(f, 8192)).IsEmpty;)
        {
            all.AddRange(block.Span);
        }

        await init.CloseAsync(f);
        return Encoding.UTF8.GetString([.. all]);
    }
}
