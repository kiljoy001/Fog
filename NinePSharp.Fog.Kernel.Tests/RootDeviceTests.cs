using NinePSharp.Constants;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class RootDeviceTests
{
    private static readonly ResourceOperationContext Context = new(new ResourceOperationId("test", 1), 1, "glenda");
    private readonly RootDevice root = new(() => "glenda");

    [Fact]
    public async Task TheDirectoryIsHashSlashAndEachMountPointIsNamedForItself()
    {
        Assert.Equal(("#/", 0x80000000u | 0b101_101_101, "glenda"), Describe(await root.StatAsync(RootDevice.Root, default)));
        Assert.Equal(("bin", 0x80000000u | 0b101_101_101, "glenda"), Describe(await root.StatAsync(RootDevice.MountPoint("bin"), default)));
        Assert.Equal("shr", (await root.StatAsync(RootDevice.MountPoint("shr"), default)).Name);
    }

    [Fact]
    public async Task EveryMountPointIsWalkedToFromTheRootAndNothingFromAMountPoint()
    {
        Assert.Equal(RootDevice.MountPoint("bin"), await root.WalkAsync(RootDevice.Root, "bin", default));
        Assert.Equal(RootDevice.MountPoint("shr"), await root.WalkAsync(RootDevice.Root, "shr", default));
        Assert.Null(await root.WalkAsync(RootDevice.Root, "boot", default));
        Assert.Null(await root.WalkAsync(RootDevice.MountPoint("n"), "bin", default));
    }

    [Fact]
    public async Task OnlyTheRootListsEntries()
    {
        Assert.Equal(13, (await root.ReadDirectoryAsync(RootDevice.Root, default)).Count);
        Assert.Empty(await root.ReadDirectoryAsync(RootDevice.MountPoint("n"), default));
    }

    [Fact]
    public async Task ADirectoryOpensForReadingOnly()
    {
        Assert.Equal(NinePConstants.OREAD, (await root.OpenAsync(RootDevice.Root, NinePConstants.OREAD, Context, default)).Mode);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => root.OpenAsync(RootDevice.Root, NinePConstants.OWRITE, Context, default)).Message);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => root.OpenAsync(RootDevice.Root, NinePConstants.ORDWR, Context, default)).Message);
    }

    private static (string Name, uint Mode, string User) Describe(ResourceStat stat) => (stat.Name, stat.Mode, stat.User);
}
