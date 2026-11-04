using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class ProcessTests
{
    private const string TmGmt = "GMT 0 GMT 0\n0\n\n";

    // bootrc's lines: mntgen run for /n, /mnt and /mnt/exportfs in turn, and the service each posts
    // made writable by all, unless it failed.
    [Fact]
    public async Task BootRunsMntgenForEachMountPointAndOpensWhatEachPosts()
    {
        var runs = new List<string>();
        var programs = new Dictionary<string, ProgramMain>
        {
            ["mntgen"] = async (process, argv) =>
            {
                runs.Add(string.Join(' ', argv));

                // What it leaves open, as a server does, is in its own descriptors, not init's.
                await process.CreateAsync($"/srv/{argv[2]}", NinePSharp.Constants.NinePConstants.OWRITE, 0b110_000_000);
                process.Exits(argv[2] == "slashmnt" ? "failed" : null);
            },
        };
        Process init = await FogKernel.InMemory(programs, "glenda").BootAsync();
        Assert.Equal(["mntgen -s slashn /n", "mntgen -s slashmnt /mnt", "mntgen -s mntexport /mnt/exportfs"], runs);
        Assert.Equal(0b110_110_110u, (await init.StatAsync("/srv/slashn")).Mode & 0b111_111_111);
        Assert.Equal(0b110_000_000u, (await init.StatAsync("/srv/slashmnt")).Mode & 0b111_111_111);
        Assert.Equal(0b110_110_110u, (await init.StatAsync("/srv/mntexport")).Mode & 0b111_111_111);
        Assert.Equal("glenda", (await init.StatAsync("/srv/slashn")).Uid);
        Assert.Equal(0, await init.OpenAsync("/dev/null", NinePSharp.Constants.NinePConstants.OREAD));
        await Assert.ThrowsAsync<SyscallException>(() => init.WaitAsync());
    }

    // init's cpenv: $timezone is /adm/timezone/local, which a first boot makes GMT.
    [Fact]
    public async Task InitCopiesTheLocalZoneToTimezone()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        int fd = await init.OpenAsync("/env/timezone", NinePSharp.Constants.NinePConstants.OREAD);
        Assert.Equal("GMT 0 GMT 0\n0\n\n", System.Text.Encoding.UTF8.GetString((await init.ReadAsync(fd, 100)).Span));
    }

    // dirreadall: what came before a read that fails is kept, and only a first read's failure is
    // the call's. The pipe is read once first, so that a pipe that gave the same block again would
    // give the directory read that byte, which is no entry, rather than the entry for ever.
    [Fact]
    public async Task ADirectoryReadKeepsTheEntriesBeforeAReadThatFails()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        init.Notify(note => note == "interrupt");
        (int r, int w) = await init.PipeAsync();
        await init.WriteAsync(w, "-"u8.ToArray()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("-", System.Text.Encoding.UTF8.GetString((await init.ReadAsync(r, 8192)).Span));
        var stat = new NinePSharp.Messages.Stat(0, 0, 0, new NinePSharp.Constants.Qid(NinePSharp.Constants.QidType.QTFILE, 0, 1), 0b110_100_100, 0, 0, 0, "x", "glenda", "glenda", "glenda");
        await init.WriteAsync(w, KernelFileStats.Encode(stat)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Task<IReadOnlyList<NinePSharp.Messages.Stat>> kept = init.DirReadAsync(r).AsTask();
        await Task.Delay(100);
        Assert.True(init.PostNote("interrupt"));
        Assert.Equal(["x"], (await kept.WaitAsync(TimeSpan.FromSeconds(5))).Select(entry => entry.Name));

        Task<IReadOnlyList<NinePSharp.Messages.Stat>> none = init.DirReadAsync(r).AsTask();
        await Task.Delay(100);
        Assert.True(init.PostNote("interrupt"));
        Assert.Equal("interrupted", (await Assert.ThrowsAsync<SyscallException>(() => none.WaitAsync(TimeSpan.FromSeconds(5)))).Message);
    }

    // Each call is named in the state its status shows as sysctab names it, the state before it
    // given back as it returns; a call to the host keeps the state of the call it is made in.
    [Fact]
    public async Task EachCallIsNamedAsSysctabNamesIt()
    {
        FogKernel kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>());
        Process init = await kernel.BootAsync();
        var names = new List<string?>();
        kernel.Traced = (p, state) =>
        {
            if (p == init)
            {
                names.Add(state);
            }
        };

        int fd = await init.CreateAsync("/tmp/t", NinePConstants.ORDWR, 0b110_110_110);
        await init.PWriteAsync(fd, "x"u8.ToArray(), 0);
        await init.PReadAsync(fd, 1, 0);
        NinePSharp.Messages.Stat stat = await init.FStatAsync(fd);
        await init.SeekAsync(fd, 0, 0);
        await init.Fd2PathAsync(fd);
        int dup = await init.DupAsync(fd, -1);
        await init.RenumberAsync(dup, 9);
        await init.CloseAsync(9);
        await init.FWStatAsync(fd, stat);
        await init.CloseAsync(fd);
        await init.StatAsync("/tmp/t");
        await init.WStatAsync("/tmp/t", stat);
        await init.CloseAsync(await init.OpenAsync("/tmp/t", NinePConstants.OREAD));
        await init.PipeAsync();
        await init.ChdirAsync("/tmp");
        await init.BindAsync("/tmp", "/mnt", NinePSharp.Namespaces.MountFlags.Replace);
        await init.UnmountAsync("/tmp", "/mnt");
        await init.RemoveAsync("/tmp/t");
        await init.RforkAsync(RforkFlags.Fdg);
        init.Fork(RforkFlags.Proc, _ => Task.CompletedTask);
        await init.WaitAsync();
        await init.SleepAsync(0);
        await init.AlarmAsync(0);
        await init.HostAsync(_ => Task.FromResult(0));
        Assert.Equal(
            "Create Pwrite Pread Fstat Seek Fd2path Dup Dup Close Fwstat Close Stat Wstat Open Close Pipe Chdir Bind Open Close Unmount Remove Rfork Rfork Await Sleep Alarm none",
            string.Join(' ', names.Select(n => n ?? "none")));
    }

    // A boot writes /lib/namespace and /adm/timezone/GMT again, and keeps the local zone an earlier
    // boot left, which init copies to $timezone; the first boot makes local GMT.
    [Fact]
    public async Task ABootRewritesTheNamespaceAndGmtAndKeepsLocal()
    {
        var files = new RamFs("ram", "#R", "none");
        Process first = await new FogKernel(new Dictionary<string, ProgramMain>(), files, files.Root).BootAsync();
        Assert.Equal(TmGmt, await TextAsync(first, "/adm/timezone/local"));
        Assert.Equal(TmGmt, await TextAsync(first, "/env/timezone"));
        await WriteAsync(first, "/adm/timezone/local", "EST -18000 EDT -14400\n");
        await WriteAsync(first, "/adm/timezone/GMT", "changed");
        await WriteAsync(first, "/lib/namespace", "changed");

        Process second = await new FogKernel(new Dictionary<string, ProgramMain>(), files, files.Root).BootAsync();
        Assert.Equal(TmGmt, await TextAsync(second, "/adm/timezone/GMT"));
        Assert.Equal(FogKernel.NamespaceFile, await TextAsync(second, "/lib/namespace"));
        Assert.Equal("EST -18000 EDT -14400\n", await TextAsync(second, "/adm/timezone/local"));
        Assert.Equal("EST -18000 EDT -14400\n", await TextAsync(second, "/env/timezone"));
        Assert.True(((await second.StatAsync("/adm/timezone")).Qid.Type & QidType.QTDIR) != 0);
    }

    // A process in none of its calls is Running in its status.
    [Fact]
    public async Task AProcessInNoCallIsRunning()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        var waiting = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        long pid = init.Fork(RforkFlags.Proc, async _ =>
        {
            started.SetResult();
            await waiting.Task;
        });
        await started.Task;
        int fd = await init.OpenAsync($"/proc/{pid}/status", NinePConstants.OREAD);
        string status = System.Text.Encoding.UTF8.GetString((await init.ReadAsync(fd, 512)).Span);
        waiting.SetResult();
        Assert.Equal("Running", status[56..63]);
    }

    // A process in one of its calls has the call's name for its state, as devproc gives psstate.
    [Fact]
    public async Task AProcessInACallHasTheCallsNameInItsStatus()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        var started = new TaskCompletionSource();
        long pid = init.Fork(RforkFlags.Proc, async child =>
        {
            started.SetResult();
            await child.SleepAsync(2000);
        });
        await started.Task;
        string state = "Running";
        for (int i = 0; i < 100 && state.TrimEnd() == "Running"; i++)
        {
            await Task.Delay(10);
            int fd = await init.OpenAsync($"/proc/{pid}/status", NinePConstants.OREAD);
            state = System.Text.Encoding.UTF8.GetString((await init.ReadAsync(fd, 512)).Span)[56..67];
            await init.CloseAsync(fd);
        }

        Assert.Equal("Sleep      ", state);
        await init.WaitAsync();
    }

    [Fact]
    public async Task ExecWithoutArgumentsIsABadArgument()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        var error = await Assert.ThrowsAsync<SyscallException>(() => init.ExecAsync("/bin/true", []));
        Assert.Equal("bad arg in system call", error.Message);
    }

    [Fact]
    public async Task AStatRecordIsSizedForItsStrings()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        Assert.Equal(49 + "tmp".Length + (3 * "glenda".Length), (await init.StatAsync("/tmp")).Size);
    }

    // sysstat names the entry by pathlast of the name the file was reached by: nothing for /, and
    // the record's size follows the name.
    [Fact]
    public async Task AStatIsNamedByTheLastElementOfTheNameItWasReachedBy()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        await init.ChdirAsync("/tmp");
        Assert.Equal(
            [string.Empty, string.Empty, "tmp", "tmp", "#c", "cons", "tmp"],
            new[] { await init.StatAsync("/"), await init.StatAsync("/tmp/.."), await init.StatAsync("/tmp/"), await init.StatAsync("."), await init.StatAsync("#c"), await init.StatAsync("#c/cons"), await init.StatAsync("../tmp") }.Select(d => d.Name));
        Assert.Equal((await init.StatAsync("/tmp")).Size - "tmp".Length, (await init.StatAsync("/")).Size);
    }

    // A directory read's records carry the type and dev a stat of each file gives, as devdir's do.
    [Fact]
    public async Task ADirectoryReadGivesEachEntryItsDevice()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        int fd = await init.OpenAsync("/dev", NinePSharp.Constants.NinePConstants.OREAD);
        NinePSharp.Messages.Stat cons = (await init.DirReadAsync(fd)).Single(d => d.Name == "cons");
        NinePSharp.Messages.Stat stat = await init.StatAsync("/dev/cons");
        Assert.Equal(('c', stat.Dev), ((char)cons.Type, cons.Dev));
    }

    // create with OEXCL refuses a file that is there, a device's or the store's, by its name.
    [Fact]
    public async Task AnExclusiveCreateOfAFileThatIsThereIsRefused()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        await init.CloseAsync(await init.CreateAsync("/tmp/x", NinePConstants.OWRITE, 0b110_110_110));
        foreach (string path in new[] { "/tmp/x", "/dev/null" })
        {
            Assert.Equal($"file already exists: '{path}'", (await Assert.ThrowsAsync<SyscallException>(async () => await init.CreateAsync(path, NinePConstants.OREAD | NinePConstants.OEXCL, 0b110_110_110))).Message);
        }
    }

    // sysseek refuses a pipe, whichever end and wherever to, and a closed descriptor as fdtochan does.
    [Fact]
    public async Task ASeekOnAPipeIsRefused()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        var (first, second) = await init.PipeAsync();
        foreach ((int fd, int whence) in new[] { (first, 0), (second, 2) })
        {
            Assert.Equal("seek on a stream", (await Assert.ThrowsAsync<SyscallException>(async () => await init.SeekAsync(fd, 0, whence))).Message);
        }

        Assert.Equal("fd out of range or not open", (await Assert.ThrowsAsync<SyscallException>(async () => await init.SeekAsync(9, 0, 0))).Message);
        Assert.Equal(0, await init.SeekAsync(await init.OpenAsync("/dev/null", 0), 0, 2));
    }

    [Fact]
    public void NullDirSetsEveryNumberToAllOnesAndEveryStringEmpty()
    {
        NinePSharp.Messages.Stat dir = Process.NullDir;
        Assert.Equal((ushort.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, ulong.MaxValue), (dir.Type, dir.Dev, dir.Mode, dir.Atime, dir.Mtime, dir.Length));
        Assert.Equal((0xFF, uint.MaxValue, ulong.MaxValue), ((int)dir.Qid.Type, dir.Qid.Version, dir.Qid.Path));
        Assert.All(new[] { dir.Name, dir.Uid, dir.Gid, dir.Muid }, Assert.Empty);
    }

    [Fact]
    public async Task ForkRejectsFlagsTheKernelDoesNotImplement()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        var error = Assert.Throws<NotSupportedException>(() => init.Fork((RforkFlags)(1 << 20), process => Task.CompletedTask));
        Assert.Equal("rfork flags 1048576 are not implemented", error.Message);
    }

    [Fact]
    public async Task APipeDescriptorReportsItsReadinessAndAnyOtherNone()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        var (first, second) = await init.PipeAsync();
        await init.WriteAsync(first, "go"u8.ToArray()).AsTask().WaitAsync(KernelDriver.Bound);
        Assert.Equal((2L, false), await init.PipeReadinessAsync(second));
        await init.CloseAsync(first);
        Assert.Equal((2L, true), await init.PipeReadinessAsync(second));
        Assert.Null(await init.PipeReadinessAsync(await init.OpenAsync("/dev/null", 0)));
        Assert.Equal("fd out of range or not open", (await Assert.ThrowsAsync<SyscallException>(async () => await init.PipeReadinessAsync(9))).Message);
    }

    private static async Task<string> TextAsync(Process p, string path)
    {
        int fd = await p.OpenAsync(path, NinePConstants.OREAD);
        string text = System.Text.Encoding.UTF8.GetString((await p.ReadAsync(fd, 8192)).Span);
        await p.CloseAsync(fd);
        return text;
    }

    private static async Task WriteAsync(Process p, string path, string text)
    {
        int fd = await p.OpenAsync(path, NinePConstants.OWRITE | NinePConstants.OTRUNC);
        await p.WriteAsync(fd, System.Text.Encoding.UTF8.GetBytes(text));
        await p.CloseAsync(fd);
    }
}
