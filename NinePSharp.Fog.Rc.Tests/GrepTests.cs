using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// grep on bytes that are no UTF-8, which rc's strings cannot give it: a class whose first range
// runs backwards matches the byte 0xff, as 9front's grep matches it, and one past the first is
// passed over.
public sealed class GrepTests : IAsyncLifetime
{
    private SecstoreHarness h = null!;

    public async Task InitializeAsync() => h = await SecstoreHarness.StartAsync();

    public async Task DisposeAsync() => await h.DisposeAsync();

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("[z-a]", "1\n")]
    [InlineData("[bz-a]", "1\n")]
    [InlineData("[y-ab]", "1\n")]
    public async Task ABackwardRangeMatchesOnlyTheByte0xffAndOnlyFirst(string pattern, string count)
    {
        await h.WriteFileAsync("/tmp/ff", [(byte)'b', (byte)'\n', 0xff, (byte)'\n']);
        Assert.Equal((count, string.Empty), await h.RunAsync(string.Empty, "grep", "-c", pattern, "/tmp/ff"));
    }
}
