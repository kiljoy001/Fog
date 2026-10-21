using System.Globalization;
using System.Numerics;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

// devproc's walks, listings, stats and permissions, and how it reads and writes args, note and noteid.
public sealed class ProcDeviceTests : IAsyncDisposable
{
    private const uint Directory = (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101;

    private readonly List<Agent> agents = [];
    private FogKernel kernel = null!;
    private Process init = null!;
    private ProcDevice proc = null!;

    [Fact]
    public async Task APidWalksOnlyAsTheKernelWritesItAndOnlyWhileTheProcessIsThere()
    {
        await BootAsync();

        Assert.NotNull(await proc.WalkAsync(ProcDevice.Root, Format(init.Pid), default));
        Assert.Null(await proc.WalkAsync(ProcDevice.Root, "0" + Format(init.Pid), default));
        Assert.Null(await proc.WalkAsync(ProcDevice.Root, "999999", default));
    }

    [Fact]
    public async Task ListingsGiveEachProcessInPidOrderAndEachFileAsWalksFindThem()
    {
        await BootAsync();
        Agent child = await StartAsync();
        IReadOnlyList<ResourceDirectoryEntry> processes = await proc.ReadDirectoryAsync(ProcDevice.Root, default);
        ResourceHandle directory = (await proc.WalkAsync(ProcDevice.Root, Format(child.Pid), default))!;
        IReadOnlyList<ResourceDirectoryEntry> files = await proc.ReadDirectoryAsync(directory, default);

        Assert.Equal([Format(init.Pid), Format(child.Pid)], processes.Select(entry => entry.Name));
        Assert.Equal(directory, processes[1].Handle);
        Assert.Equal(["args", "fd", "note", "noteid", "notepg"], files.Select(entry => entry.Name));
        foreach (ResourceDirectoryEntry file in files)
        {
            Assert.Equal(await proc.WalkAsync(directory, file.Name, default), file.Handle);
        }
    }

    [Fact]
    public async Task StatsNameEachFileWithItsPermissions()
    {
        await BootAsync();
        Agent child = await StartAsync();
        ResourceHandle directory = await DirectoryAsync(child.Pid);

        Assert.Equal(("#p", Directory), Describe(await proc.StatAsync(ProcDevice.Root, default)));
        Assert.Equal((Format(child.Pid), Directory), Describe(await proc.StatAsync(directory, default)));
        Assert.Equal(("args", 0b110_110_000u), Describe(await StatAsync(child.Pid, "args")));
        Assert.Equal(("fd", 0b100_100_100u), Describe(await StatAsync(child.Pid, "fd")));
        Assert.Equal(("note", 0b110_100_000u), Describe(await StatAsync(child.Pid, "note")));
        Assert.Equal(("noteid", 0b110_110_100u), Describe(await StatAsync(child.Pid, "noteid")));
        Assert.Equal(("notepg", 0b110_100_000u), Describe(await StatAsync(child.Pid, "notepg")));
    }

    [Fact]
    public async Task AnExitedProcessesDirectoryNoLongerOpens()
    {
        await BootAsync();
        Agent child = await StartAsync();
        ResourceHandle directory = await DirectoryAsync(child.Pid);
        child.Stop();
        await init.WaitAsync().WaitAsync(KernelDriver.Bound);

        Assert.Equal(Errors.ProcessExited, (await Assert.ThrowsAsync<IOException>(() => proc.OpenAsync(directory, NinePConstants.OREAD, Context(0, "eve"), default).AsTask())).Message);
        Assert.Equal(NinePConstants.OREAD, (await proc.OpenAsync(ProcDevice.Root, NinePConstants.OREAD, Context(0, "eve"), default)).Mode);
    }

    [Fact]
    public async Task TheOwnerTheHostOwnerAndOthersEachHaveTheirOwnPermissions()
    {
        await BootAsync();
        Agent child = await StartAsync();
        child.Process.User = "glenda";

        Assert.Null(await OpenFailureAsync(child.Pid, "args", NinePConstants.OWRITE, "glenda"));
        Assert.Null(await OpenFailureAsync(child.Pid, "args", NinePConstants.OWRITE, "eve"));
        Assert.Equal(Errors.Permission, await OpenFailureAsync(child.Pid, "note", NinePConstants.OWRITE, "eve"));
        Assert.Equal(Errors.Permission, await OpenFailureAsync(child.Pid, "args", NinePConstants.OREAD, "ada"));
        Assert.Null(await OpenFailureAsync(child.Pid, "noteid", NinePConstants.OREAD, "ada"));
    }

    [Fact]
    public async Task NoneMayOpenItsOwnFilesAndAnothersWhenTheHostOwnerIsNone()
    {
        await BootAsync();
        Agent child = await StartAsync();
        child.Process.User = "none";
        Assert.Null(await OpenFailureAsync(child.Pid, "args", NinePConstants.OREAD, "none", child.Pid));
        Assert.Equal(Errors.Permission, await OpenFailureAsync(init.Pid, "args", NinePConstants.OREAD, "none", child.Pid));

        await BootAsync("none");
        Agent other = await StartAsync();
        Assert.Null(await OpenFailureAsync(init.Pid, "args", NinePConstants.OREAD, "none", other.Pid));
    }

    [Fact]
    public async Task ADirectoryReadsAsNothing()
    {
        await BootAsync();
        ResourceHandle directory = await DirectoryAsync(init.Pid);

        Assert.True((await ReadAsync(ProcDevice.Root, 0, 100)).IsEmpty);
        Assert.True((await ReadAsync(directory, 0, 100)).IsEmpty);
    }

    [Fact]
    public async Task ANoteReadInOneByteGivesOnlyItsNulAndNoByteIsTooSmall()
    {
        await BootAsync();
        Agent child = await StartAsync();
        Assert.True(child.Process.PostNote("hangup"));
        ResourceHandle note = await FileAsync(child.Pid, "note");

        Assert.Equal(Errors.TooSmall, (await Assert.ThrowsAsync<IOException>(() => ReadAsync(note, 0, 0))).Message);
        Assert.Equal([0], (await ReadAsync(note, 0, 1)).ToArray());
        Assert.True((await ReadAsync(note, 0, 1)).IsEmpty);
    }

    [Fact]
    public async Task FdStillGivesADescriptorWhoseFileHasBeenRemoved()
    {
        await BootAsync();
        int srv = await init.CreateAsync("/srv/gone", NinePConstants.OWRITE, 0b110_110_110);
        await init.RemoveAsync("/srv/gone");

        int fd = await init.OpenAsync($"/proc/{init.Pid}/fd", NinePConstants.OREAD);
        var lines = new List<string>();
        for (string line; (line = Encoding.UTF8.GetString((await init.ReadAsync(fd, 8192)).Span)).Length > 0;)
        {
            lines.Add(line);
        }

        Assert.Equal($"{srv,3} w  s    0 (0000000000000004 0 00)     0        0 /srv/gone\n", lines[srv + 1]);
    }

    [Fact]
    public async Task FdGivesDotAndEachOpenDescriptorALineAtATime()
    {
        await BootAsync();
        await init.ChdirAsync("/tmp");
        int file = await init.CreateAsync("/tmp/f", NinePConstants.ORDWR, 0b110_110_110);
        await init.WriteAsync(file, "hello"u8.ToArray());
        int dir = await init.OpenAsync("/tmp", NinePConstants.OREAD);
        int null0 = await init.OpenAsync("/dev/null", NinePConstants.OWRITE);
        await init.CloseAsync(await init.CreateAsync("/tmp/x", NinePConstants.OWRITE, 0b111_111_111));
        int exec = await init.OpenAsync("/tmp/x", NinePConstants.OEXEC);
        (int pipe, _) = await init.PipeAsync();
        await init.CloseAsync(null0);
        await init.DirReadAsync(dir);

        int fd = await init.OpenAsync($"/proc/{init.Pid}/fd", NinePConstants.OREAD);
        var lines = new List<string>();
        for (string line; (line = Encoding.UTF8.GetString((await init.ReadAsync(fd, 8192)).Span)).Length > 0;)
        {
            lines.Add(line);
        }

        Assert.Equal(
            [
                "/tmp\n",
                "  0 rw R    0 (0000000000000008 2 00)     0        5 /tmp/f\n",
                "  1 r  R    0 (0000000000000005 0 80)     0        0 /tmp\n",
                "  2 r  p    0 (000000000000000a 0 00)     0      123 /proc/1/fd\n",
                "  3  R    0 (0000000000000009 0 00)     0        0 /tmp/x\n",
                "  4 rw |    1 (0000000000000000 0 00)     0        0 #|/data\n",
                "  5 rw |    1 (0000000000000001 0 00)     0        0 #|/data1\n",
            ],
            lines);
        Assert.Equal($"permission denied: '/proc/{init.Pid}/fd'", (await Assert.ThrowsAsync<SyscallException>(() => init.OpenAsync($"/proc/{init.Pid}/fd", NinePConstants.OWRITE).AsTask())).Message);
        ResourceOpenHandle opened = await proc.OpenAsync(await FileAsync(init.Pid, "fd"), NinePConstants.OREAD, Context(init.Pid, "eve"), default);
        Assert.Equal("tm", Encoding.UTF8.GetString((await proc.ReadAsync(opened, 1, 2, default)).Span));
        Assert.Equal("mp\n", Encoding.UTF8.GetString((await proc.ReadAsync(opened, 2, 100, default)).Span));
        Assert.Empty((await proc.ReadAsync(opened, 100_000, 100, default)).ToArray());
        _ = (file, dir, exec, pipe);
    }

    [Fact]
    public async Task ArgsReadsFromAnyOffsetAndAnyCount()
    {
        await BootAsync();
        Agent child = await StartAsync();
        await WriteAsync(child.Pid, "args", "waiting"u8.ToArray());
        byte[] args = Encoding.UTF8.GetBytes(child.Process.Args);
        ResourceHandle file = await FileAsync(child.Pid, "args");

        Assert.Equal(args, (await ReadAsync(file, 0, 1000)).ToArray());
        Assert.Equal(args[1..], (await ReadAsync(file, 1, 1000)).ToArray());
        Assert.Equal(args[2..5], (await ReadAsync(file, 2, 3)).ToArray());
        Assert.True((await ReadAsync(file, (ulong)args.Length + 5, 10)).IsEmpty);
    }

    [Fact]
    public async Task ArgsAndNoteidAreWrittenWholeAndWhatIsWrittenEndsAtANul()
    {
        await BootAsync();
        Agent child = await StartAsync();

        Assert.Equal(Errors.TooBig, await WriteFailureAsync(child.Pid, "args", "x"u8.ToArray(), 1));
        Assert.Equal(Errors.TooBig, await WriteFailureAsync(child.Pid, "noteid", "1"u8.ToArray(), 1));
        await WriteAsync(child.Pid, "args", "x\0y"u8.ToArray());
        Assert.EndsWith("[x]", child.Process.Args, StringComparison.Ordinal);
        await WriteAsync(child.Pid, "args", "\0y"u8.ToArray());
        Assert.EndsWith("[]", child.Process.Args, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" \t\n\f\r\v{0}", null)]
    [InlineData("+{0}", null)]
    [InlineData("{0} and more", null)]
    [InlineData("-{0}", Errors.Permission)]
    [InlineData("1{0}", Errors.Permission)]
    [InlineData(" {0}", Errors.BadArg)]
    public async Task ANoteIdIsReadAsAtoiReadsIt(string text, string? error)
    {
        await BootAsync();
        Agent child = await StartAsync();

        Assert.Equal(error, await WriteFailureAsync(child.Pid, "noteid", Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture, text, child.Pid))));
        Assert.Equal(error is null ? child.Pid : init.NoteId, child.Process.NoteId);
    }

    [Fact]
    public async Task ANoteIdTooLargeForALongIsHeldToItsLargest()
    {
        await BootAsync();
        Agent child = await StartAsync();
        string wrapped = ((BigInteger.One << 64) + init.NoteId).ToString(CultureInfo.InvariantCulture);
        await WriteAsync(child.Pid, "noteid", Encoding.ASCII.GetBytes(Format(child.Pid)));

        Assert.Equal(Errors.Permission, await WriteFailureAsync(child.Pid, "noteid", Encoding.ASCII.GetBytes(wrapped)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Agent agent in agents)
        {
            agent.Stop();
        }

        await Task.CompletedTask;
    }

    private static string Format(long pid) => pid.ToString(CultureInfo.InvariantCulture);

    private static ResourceOperationContext Context(long pid, string user) => new(new ResourceOperationId("test", 1), pid, user);

    private static (string Name, uint Mode) Describe(ResourceStat stat) => (stat.Name, stat.Mode);

    private async Task BootAsync(string user = "eve")
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(), user);
        init = await kernel.BootAsync();
        proc = new ProcDevice(kernel);
    }

    private async Task<Agent> StartAsync()
    {
        var agent = new Agent(init, true);
        agents.Add(agent);
        await agent.StartAsync();
        return agent;
    }

    private async Task<ResourceHandle> DirectoryAsync(long pid) => (await proc.WalkAsync(ProcDevice.Root, Format(pid), default))!;

    private async Task<ResourceHandle> FileAsync(long pid, string name) => (await proc.WalkAsync(await DirectoryAsync(pid), name, default))!;

    private async Task<ResourceStat> StatAsync(long pid, string name) => await proc.StatAsync(await FileAsync(pid, name), default);

    private async Task<string?> OpenFailureAsync(long pid, string name, byte mode, string user, long caller = 0)
    {
        try
        {
            await proc.OpenAsync(await FileAsync(pid, name), mode, Context(caller, user), default);
            return null;
        }
        catch (IOException error)
        {
            return error.Message;
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadAsync(ResourceHandle resource, ulong offset, uint count)
    {
        ResourceOpenHandle open = await proc.OpenAsync(resource, NinePConstants.OREAD, Context(0, kernel.User), default);
        return await proc.ReadAsync(open, offset, count, default);
    }

    private async Task WriteAsync(long pid, string name, byte[] data) => Assert.Null(await WriteFailureAsync(pid, name, data));

    private async Task<string?> WriteFailureAsync(long pid, string name, byte[] data, ulong offset = 0)
    {
        try
        {
            ResourceOpenHandle open = await proc.OpenAsync(await FileAsync(pid, name), NinePConstants.OWRITE, Context(0, kernel.User), default);
            await proc.WriteAsync(open, offset, data, Context(0, kernel.User), default);
            return null;
        }
        catch (IOException error)
        {
            return error.Message;
        }
    }
}
