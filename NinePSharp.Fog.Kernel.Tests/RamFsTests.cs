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

    // lib9p: a create needs the directory writable, before the name is looked for, and a remove
    // needs the parent writable.
    [Fact]
    public async Task CreateAndRemoveNeedTheDirectoryWritable()
    {
        var fs = new RamFs("ram", "#R", "glenda");
        ResourceHandle d = (await fs.CreateAndOpenAsync(fs.Root, "d", (uint)NinePSharp.Constants.NinePConstants.FileMode9P.DMDIR | 0b111_111_111, 0, Context, default)).Resource;
        ResourceHandle f = (await fs.CreateAndOpenAsync(d, "f", 0b110_110_110, 1, Context, default)).Resource;
        await fs.WStatAsync(d, ResourceWStat.Unchanged() with { Mode = (uint)NinePSharp.Constants.NinePConstants.FileMode9P.DMDIR | 0b101_101_101 }, Context, default);
        foreach (string name in new[] { "g", "f" })
        {
            Assert.Equal("permission denied", (await Assert.ThrowsAsync<ResourceCreateRejectedException>(() => fs.CreateAndOpenAsync(d, name, 0b110_110_110, 1, Context, default).AsTask())).Message);
        }

        Assert.Equal("permission denied", (await Assert.ThrowsAsync<IOException>(() => fs.RemoveAsync(f, null, Context, default).AsTask())).Message);
        Assert.Equal(f, await fs.WalkAsync(d, "f", default));
    }
}
