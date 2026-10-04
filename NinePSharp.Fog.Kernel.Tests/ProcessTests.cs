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
    public async Task ForkRejectsFlagsTheKernelDoesNotImplement()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        var error = Assert.Throws<NotSupportedException>(() => init.Fork(RforkFlags.Nomnt, process => Task.CompletedTask));
        Assert.Equal("rfork flags Nomnt are not implemented", error.Message);
    }
}
