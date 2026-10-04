using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class DupDeviceTests
{
    private static readonly ResourceOperationContext Context = new(new ResourceOperationId("test", 1), 1, "glenda");

    [Fact]
    public async Task TheDirectoryStatsAsDot()
    {
        DupDevice device = NewDevice();
        ResourceStat stat = await device.StatAsync(DupDevice.Root, default);
        Assert.Equal(".", stat.Name);
        Assert.Equal(0x80000000u | 0b101_101_101, stat.Mode);
    }

    [Fact]
    public async Task CtlFilesCannotBeOpenedYetAndNothingIsWritable()
    {
        DupDevice device = NewDevice();
        var ctl = new ResourceHandle(new ResourceIdentity(DupDevice.Provider, "#d", 2), NinePSharp.Constants.QidType.QTFILE);
        Assert.Equal("ctl files are not implemented", Assert.Throws<NotSupportedException>(() => device.OpenAsync(ctl, 0, Context, default)).Message);
        Assert.Throws<NotSupportedException>(() => device.ReadAsync(new ResourceOpenHandle(ctl, "h", 0, 0), 0, 1, default));
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => device.WriteAsync(new ResourceOpenHandle(ctl, "h", 1, 0), 0, new byte[1], Context, default)).Message);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => device.CreateAsync(DupDevice.Root, "x", false, default)).Message);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => device.CreateAndOpenAsync(DupDevice.Root, "x", 0, 0, Context, default)).Message);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => device.RemoveAsync(ctl, null, Context, default)).Message);
        await device.ClunkAsync(new ResourceOpenHandle(DupDevice.Root, "h", 0, 0), Context, default);
    }

    [Fact]
    public async Task ADescriptorAndItsCtlFileStatByName()
    {
        var table = new VProcessTable();
        var files = new RamFs("ram", "#R", "glenda");
        VProcess process = table.CreateInitial(new NamespaceNavigator(new MountTable(), files).Attach(files.Root));
        ResourceOpenHandle opened = await files.CreateAndOpenAsync(files.Root, "f", 0b110_110_110, 1, Context, default);
        process.Descriptors.Install(opened, () => ValueTask.CompletedTask);
        var device = new DupDevice(process, "glenda");
        ResourceHandle file = (await device.WalkAsync(DupDevice.Root, "0", default))!;
        ResourceHandle ctl = (await device.WalkAsync(DupDevice.Root, "0ctl", default))!;
        Assert.Equal(("0", 0b010_000_000u), ((await device.StatAsync(file, default)).Name, (await device.StatAsync(file, default)).Mode));
        Assert.Equal(("0ctl", 0b100_000_000u), ((await device.StatAsync(ctl, default)).Name, (await device.StatAsync(ctl, default)).Mode));
        Assert.Equal(0, Assert.Throws<DupOpenException>(() => device.OpenAsync(file, 0, Context, default)).Descriptor);
        Assert.Throws<NotSupportedException>(() => device.OpenAsync(ctl, 0, Context, default));
    }

    private static DupDevice NewDevice()
        => new DupDevice(new VProcessTable().CreateInitial(new NamespaceNavigator(new MountTable(), new RamFs("ram", "#R", "glenda")).Attach(DupDevice.Root)), "glenda");
}
