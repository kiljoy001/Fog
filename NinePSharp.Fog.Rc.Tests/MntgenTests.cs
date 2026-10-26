using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// mntgen as boot runs it for /n, spoken to on the pipe it posts as /srv/slashn: what it makes of a
// name walked to, what it lists and stats, and how its command line goes.
public sealed class MntgenTests : IAsyncLifetime
{
    private const uint NoFid = uint.MaxValue;
    private readonly ManualClock clock = new() { Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
    private FogKernel kernel = null!;
    private Process init = null!;
    private int fd = -1;
    private ushort tag;

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All) { ["rc"] = RcProgram.MainAsync }, "glenda", clock, new FakeHostNetwork(), new FakeHostConsole());
        init = await kernel.BootAsync();
        await init.CloseAsync(await init.CreateAsync("/root/rc/lib", NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));
        int rcmain = await init.CreateAsync("/root/rc/lib/rcmain", NinePConstants.OWRITE, 0b110_110_100);
        await init.WriteAsync(rcmain, Encoding.UTF8.GetBytes(RcProgram.Rcmain));
        await init.CloseAsync(rcmain);
        fd = await init.OpenAsync("/srv/slashn", NinePConstants.ORDWR);
        Assert.Equal((byte)MessageTypes.Rversion, (await RpcAsync(t => new Tversion(t, 8216, "9P2000")))[4]);
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
    public async Task OnlyTheRootIsAttachedTo()
    {
        Assert.Equal("invalid attach specifier", Error(await RpcAsync(t => new Tattach(t, 1, NoFid, "glenda", "x"))));
        Assert.Equal((QidType.QTDIR, 0ul), Qid(await RpcAsync(t => new Tattach(t, 1, NoFid, "glenda", string.Empty))));
    }

    [Fact]
    public async Task ANameWalkedToIsADirectoryWhoseQidIsItsHashAndWhichHoldsNothing()
    {
        await AttachAsync(1);
        Qid x = Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 2, ["x"]))).Wqid);
        Assert.Equal((QidType.QTDIR, Hash("x")), (x.Type, x.Path));
        Assert.Equal("path not found", Error(await RpcAsync(t => new Twalk(t, 2, 3, ["y"]))));
        Qid[] back = new Rwalk(await RpcAsync(t => new Twalk(t, 2, 4, [".."]))).Wqid;
        Assert.Equal(0ul, Assert.Single(back).Path);
        Assert.Equal(0ul, Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 5, [".."]))).Wqid).Path);
        Assert.Equal(Hash("z"), Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 6, ["z"]))).Wqid).Path);
        Assert.Equal(Hash("x"), Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 7, ["x"]))).Wqid).Path);
    }

    [Fact]
    public async Task TheRootStatsAsSlashFromWhenMntgenStartedAndANameFromWhenItWasMade()
    {
        uint booted = (uint)clock.Now.ToUnixTimeSeconds();
        clock.Now += TimeSpan.FromMinutes(1);
        await AttachAsync(1);
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 2, ["x"]))).Wqid);
        Assert.Equal(("/", 0x80000000u | 0b101_101_101, "sys", booted), StatOf(await RpcAsync(t => new Tstat(t, 1))));
        Assert.Equal(("x", 0x80000000u | 0b101_101_101, "sys", booted + 60), StatOf(await RpcAsync(t => new Tstat(t, 2))));
    }

    [Fact]
    public async Task TheRootListsTheNamesHeldAndANameListsNothing()
    {
        await AttachAsync(1);
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 2, ["x"]))).Wqid);
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 3, ["z"]))).Wqid);
        Assert.Equal("permission denied", Error(await RpcAsync(t => new Topen(t, 1, NinePConstants.OREAD | NinePConstants.ORCLOSE))));
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 1, NinePConstants.OREAD)))[4]);
        Assert.Equal(["x", "z"], Names(new Rread(await RpcAsync(t => new Tread(t, 1, 0, 8192))).Data.ToArray()));
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 2, NinePConstants.OREAD)))[4]);
        Assert.Empty(new Rread(await RpcAsync(t => new Tread(t, 2, 0, 8192))).Data.ToArray());
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 3)))[4]);
    }

    [Fact]
    public async Task ANameGoesOnceNoFidIsOnIt()
    {
        await AttachAsync(1);
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 2, ["x"]))).Wqid);
        Assert.Empty(new Rwalk(await RpcAsync(t => new Twalk(t, 2, 3, []))).Wqid);
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 2)))[4]);
        Assert.Equal(["x"], await ListAsync());
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 3)))[4]);
        Assert.Empty(await ListAsync());
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 4, ["z"]))).Wqid);
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 4, 4, [".."])))[4]);

        // A dotdot counts a fid off its name, but only a clunk lets a name go, in mntgen.c as here.
        Assert.Equal(["z"], await ListAsync());
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 4)))[4]);
        Assert.Single(new Rwalk(await RpcAsync(t => new Twalk(t, 1, 5, ["z"]))).Wqid);
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 5)))[4]);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task ItMountsOnNUnlessGivenAMountPointAndFailsAsPostsrvDoes()
    {
        Assert.Equal(("made\n", string.Empty), await RunAsync("unmount /n; mntgen -s other >/dev/null >[2=1]; test -d /n/q && echo made"));
        (string status, string error) = await RunAsync("mntgen -s slashn /tmp; echo $status");
        Assert.Equal("mntgen: postsrv: file already exists: '/srv/slashn'\n", error);
        Assert.EndsWith("postsrv: file already exists: '/srv/slashn'\n", status, StringComparison.Ordinal);
        Assert.Equal((string.Empty, "mntgen [-s srvname] [mtpt]\nmntgen [-s srvname] [mtpt]\n"), await RunAsync("mntgen -x; mntgen a b"));
    }

    private static ulong Hash(string name) => BinaryPrimitives.ReadUInt64LittleEndian(MD5.HashData(Encoding.UTF8.GetBytes(name))) & ((1UL << 48) - 1);

    private static string Error(byte[] reply)
    {
        Assert.Equal((byte)MessageTypes.Rerror, reply[4]);
        return new Rerror(reply).Ename;
    }

    private static (QidType Type, ulong Path) Qid(byte[] reply) => (new Rattach(reply).Qid.Type, new Rattach(reply).Qid.Path);

    private static (string Name, uint Mode, string Uid, uint Mtime) StatOf(byte[] reply)
    {
        Stat stat = new Rstat(reply).Stat;
        Assert.Equal((stat.Uid, stat.Mtime, 0ul, string.Empty), (stat.Gid, stat.Atime, stat.Length, stat.Muid));
        return (stat.Name, stat.Mode, stat.Uid, stat.Mtime);
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

    private static byte[] Encode(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }

    // The script run by rc in a namespace of its own, and what it printed and what it put on its error output.
    private async Task<(string Output, string Error)> RunAsync(string script)
    {
        // Init's 0, 1 and 2 held first, so the pipes' ends are none of them.
        for (int i = 0; i < 3; i++)
        {
            await init.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }

        (int out0, int out1) = await init.PipeAsync();
        (int err0, int err1) = await init.PipeAsync();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg, async child =>
        {
            await child.DupAsync(out1, 1);
            await child.DupAsync(err1, 2);
            foreach (int end in new[] { out0, out1, err0, err1 })
            {
                await child.CloseAsync(end);
            }

            await child.ExecAsync("/bin/rc", ["rc", "-c", "path=/bin; " + script]);
        });
        await init.CloseAsync(out1);
        await init.CloseAsync(err1);
        string output = await ReadAllAsync(out0), error = await ReadAllAsync(err0);
        await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return (output, error);
    }

    private async Task<string> ReadAllAsync(int f)
    {
        var text = new StringBuilder();
        for (ReadOnlyMemory<byte> b; !(b = await init.ReadAsync(f, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsEmpty;)
        {
            text.Append(Encoding.UTF8.GetString(b.Span));
        }

        await init.CloseAsync(f);
        return text.ToString();
    }

    private async Task<List<string>> ListAsync()
    {
        Assert.Equal((byte)MessageTypes.Rwalk, (await RpcAsync(t => new Twalk(t, 1, 100, [])))[4]);
        Assert.Equal((byte)MessageTypes.Ropen, (await RpcAsync(t => new Topen(t, 100, NinePConstants.OREAD)))[4]);
        string[] names = Names(new Rread(await RpcAsync(t => new Tread(t, 100, 0, 8192))).Data.ToArray());
        Assert.Equal((byte)MessageTypes.Rclunk, (await RpcAsync(t => new Tclunk(t, 100)))[4]);
        return [.. names];
    }

    private async Task AttachAsync(uint fid)
        => Assert.Equal((byte)MessageTypes.Rattach, (await RpcAsync(t => new Tattach(t, fid, NoFid, "glenda", string.Empty)))[4]);

    private async Task<byte[]> RpcAsync(Func<ushort, ISerializable> request)
    {
        ushort t = ++tag;
        await init.WriteAsync(fd, Encode(request(t)));
        byte[] reply = (await init.ReadAsync(fd, 8216).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
        Assert.Equal(t, BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5)));
        return reply;
    }
}
