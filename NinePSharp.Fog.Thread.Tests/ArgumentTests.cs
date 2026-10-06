using Xunit;

namespace NinePSharp.Fog.Thread.Tests;

public sealed class ArgumentTests
{
    [Fact]
    public void ChancreateRefusesANegativeSize() => Assert.Throws<ArgumentOutOfRangeException>(() => new Channel<int>(-1));

    [Fact]
    public async Task AltRefusesNoEntries()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Alt.AltAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Alt.NbAltAsync(null!));
    }

    [Fact]
    public void EntriesRefuseNoChannel()
    {
        Assert.Throws<ArgumentNullException>(() => Alt<int>.Send(null!, 1));
        Assert.Throws<ArgumentNullException>(() => Alt<int>.Recv(null!));
    }
}
