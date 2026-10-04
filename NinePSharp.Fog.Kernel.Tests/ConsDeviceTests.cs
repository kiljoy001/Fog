using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class ConsDeviceTests
{
    private static readonly NinePSharp.Namespaces.ResourceOperationContext Context = new(new NinePSharp.Namespaces.ResourceOperationId("test", 1), 1, "glenda");

    [Fact]
    public async Task TheDirectoryIsHashCAndNothingCanBeCreatedOrRemoved()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        var stat = await init.Cons.StatAsync(ConsDevice.Root, default);
        Assert.Equal(("#c", 0x80000000u | 0b101_101_101), (stat.Name, stat.Mode));
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => init.Cons.CreateAsync(ConsDevice.Root, "x", false, default)).Message);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => init.Cons.CreateAndOpenAsync(ConsDevice.Root, "x", 0, 0, Context, default)).Message);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => init.Cons.RemoveAsync(ConsDevice.Root, null, Context, default)).Message);
    }

    [Fact]
    public async Task EachFileStatsWithItsNameAndPermissions()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        var stats = new List<string>();
        foreach (var entry in await init.Cons.ReadDirectoryAsync(ConsDevice.Root, default))
        {
            var stat = await init.Cons.StatAsync(entry.Handle, default);
            stats.Add($"{stat.Name} {Convert.ToString(stat.Mode, 8)} {stat.User}");
        }

        Assert.Equal(["null 666 glenda", "pid 444 glenda", "ppid 444 glenda", "user 666 glenda", "zero 444 glenda"], stats);
    }
}
