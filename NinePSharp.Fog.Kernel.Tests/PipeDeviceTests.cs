using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class PipeDeviceTests
{
    private static readonly ResourceOperationContext Context = new(new ResourceOperationId("test", 1), 1, "glenda");

    [Fact]
    public async Task APipeIsReleasedWhenBothEndsAreClosed()
    {
        var device = new PipeDevice();
        var (first, second) = device.Create();
        ResourceOpenHandle a = await device.OpenAsync(first, 2, Context, default);
        ResourceOpenHandle duplicate = await device.OpenAsync(first, 2, Context, default);
        ResourceOpenHandle b = await device.OpenAsync(second, 2, Context, default);
        await device.ClunkAsync(a, Context, default);
        Assert.Equal(1, device.Count);
        await device.ClunkAsync(b, Context, default);
        Assert.Equal(1, device.Count);
        await device.ClunkAsync(duplicate, Context, default);
        Assert.Equal(0, device.Count);
    }

    [Fact]
    public void APipeHasNoPathsToWalkCreateStatOrRemove()
    {
        var device = new PipeDevice();
        ResourceHandle end = device.Create().First;
        Assert.Throws<NotSupportedException>(() => device.WalkAsync(end, "data", default));
        Assert.Throws<NotSupportedException>(() => device.ReadDirectoryAsync(end, default));
        Assert.Throws<NotSupportedException>(() => device.CreateAsync(end, "x", false, default));
        Assert.Throws<NotSupportedException>(() => device.StatAsync(end, default));
        Assert.Throws<NotSupportedException>(() => device.CreateAndOpenAsync(end, "x", 0, 0, Context, default));
        Assert.Throws<NotSupportedException>(() => device.RemoveAsync(end, null, Context, default));
    }
}
