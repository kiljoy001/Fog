using NinePSharp.Fog.Kernel.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class ProcessTests
{
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
}
