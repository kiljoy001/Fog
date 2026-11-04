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

    // /env's names, as envcreate takes them: one and its NUL in genbuf's 128 bytes, counted in bytes;
    // any other ramfs takes a longer one.
    [Fact]
    public async Task EnvTakesNamesOfUpTo127Bytes()
    {
        var env = new RamFs("env", "#e", "glenda", changeable: false);
        await env.CreateAndOpenAsync(env.Root, new string('v', 127), 0b110_110_110, 1, Context, default);
        await env.CreateAndOpenAsync(env.Root, new string('é', 63) + "v", 0b110_110_110, 1, Context, default);
        Assert.Equal("name too long", (await Assert.ThrowsAsync<IOException>(() => env.CreateAndOpenAsync(env.Root, new string('v', 128), 0b110_110_110, 1, Context, default).AsTask())).Message);
        Assert.Equal("name too long", (await Assert.ThrowsAsync<IOException>(() => env.CreateAndOpenAsync(env.Root, new string('é', 64), 0b110_110_110, 1, Context, default).AsTask())).Message);

        var ram = new RamFs("ram", "#R", "glenda");
        await ram.CreateAndOpenAsync(ram.Root, new string('v', 200), 0b110_110_110, 1, Context, default);
    }

    // ramfs: an append-only file is not truncated by an open with OTRUNC, and is written at its
    // end whatever the offset; any other file is truncated and written where it is asked.
    [Fact]
    public async Task AnAppendOnlyFileIsNotTruncatedAndIsWrittenAtItsEnd()
    {
        var fs = new RamFs("ram", "#R", "glenda");
        foreach ((uint mode, string expected) in new[] { (0x40000000u | 0b110_110_110, "oldnew"), (0b110_110_110u, "new") })
        {
            ResourceOpenHandle created = await fs.CreateAndOpenAsync(fs.Root, $"f{mode}", mode, 1, Context, default);
            await fs.WriteAsync(created, 0, "old"u8.ToArray(), Context, default);
            ResourceOpenHandle opened = await fs.OpenAsync(created.Resource, NinePSharp.Constants.NinePConstants.OWRITE | NinePSharp.Constants.NinePConstants.OTRUNC, Context, default);
            await fs.WriteAsync(opened, 0, "new"u8.ToArray(), Context, default);
            Assert.Equal(expected, System.Text.Encoding.UTF8.GetString((await fs.ReadAsync(opened, 0, 100, default)).Span));
        }
    }
}
