using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class ProcessTests
{
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
        var error = Assert.Throws<NotSupportedException>(() => init.Fork(RforkFlags.Nomnt, process => Task.CompletedTask));
        Assert.Equal("rfork flags Nomnt are not implemented", error.Message);
    }
}
