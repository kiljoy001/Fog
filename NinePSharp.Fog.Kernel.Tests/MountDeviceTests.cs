using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

// devmnt's connections over a pipe's end: a mount takes a reference to the end only while its
// connection lasts, so the end closes once the process and the connection have let it go.
public sealed class MountDeviceTests
{
    [Fact]
    public async Task ASecondMountOfAConnectedEndLetsGoOfItsReference()
    {
        (Process init, VProcess process, int end, int other) = await BootAsync();
        await using var peer = new ScriptedPeer();
        var mounts = new MountDevice();
        DescriptorLease first = process.Descriptors.Acquire(end);
        MountConnection connection = await mounts.ConnectAsync(first, peer, () => ScriptedPeer.Context).WaitAsync(KernelDriver.Bound);

        Assert.Same(connection, await mounts.ConnectAsync(process.Descriptors.Acquire(end), peer, () => ScriptedPeer.Context).WaitAsync(KernelDriver.Bound));
        await init.CloseAsync(end);
        await first.DisposeAsync();
        Assert.True((await init.ReadAsync(other, 1).AsTask().WaitAsync(KernelDriver.Bound)).IsEmpty);
    }

    [Fact]
    public async Task AMountWhoseVersionFailsLetsGoOfItsReference()
    {
        (Process init, VProcess process, int end, int other) = await BootAsync();
        await using var peer = new ScriptedPeer { Version = (tag, _) => new Rerror(tag, "no version") };

        await Assert.ThrowsAsync<IOException>(() => new MountDevice().ConnectAsync(process.Descriptors.Acquire(end), peer, () => ScriptedPeer.Context).WaitAsync(KernelDriver.Bound));
        await init.CloseAsync(end);
        Assert.True((await init.ReadAsync(other, 1).AsTask().WaitAsync(KernelDriver.Bound)).IsEmpty);
    }

    [Fact]
    public void AMountCreatesOnlyByOpening()
    {
        var error = Assert.Throws<NotSupportedException>(() => new MountDevice().CreateAsync(RootDevice.Root, "x", false, default));

        Assert.Equal("a mount creates only by opening what it creates", error.Message);
    }

    private static async Task<(Process Init, VProcess Process, int End, int Other)> BootAsync()
    {
        FogKernel kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda");
        Process init = await kernel.BootAsync();
        var (end, other) = await init.PipeAsync();
        return (init, kernel.Table.Get(init.Pid), end, other);
    }
}
