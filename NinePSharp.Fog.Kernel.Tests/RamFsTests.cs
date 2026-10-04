using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class RamFsTests
{
    private static readonly ResourceOperationContext Context = new(new ResourceOperationId("test", 1), 1, "glenda");

    [Fact]
    public async Task CreateMakesFilesAndDirectories()
    {
        var fs = new RamFs("ram", "#R", "glenda");
        ResourceHandle directory = await fs.CreateAsync(fs.Root, "d", true, default);
        ResourceHandle file = await fs.CreateAsync(directory, "f", false, default);
        Assert.True(directory.IsDirectory);
        Assert.False(file.IsDirectory);
        Assert.Equal(file, await fs.WalkAsync(directory, "f", default));
        Assert.Equal(0b110_110_110u, (await fs.StatAsync(file, default)).Mode);
    }

    [Fact]
    public async Task CreateAndOpenRejectsAnExistingName()
    {
        var fs = new RamFs("ram", "#R", "glenda");
        await fs.CreateAndOpenAsync(fs.Root, "f", 0b110_110_110, 1, Context, default);
        var rejected = await Assert.ThrowsAsync<ResourceCreateRejectedException>(() => fs.CreateAndOpenAsync(fs.Root, "f", 0b110_110_110, 1, Context, default).AsTask());
        Assert.Equal("file already exists", rejected.Message);
    }
}
