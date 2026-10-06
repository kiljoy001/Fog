using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class KernelFileStatsTests
{
    [Theory]
    [InlineData(60, 60)]
    [InlineData(59, 2)]
    public void AStatRecordComesWholeOnlyWhenTheBufferHoldsIt(uint count, int length)
        => Assert.Equal(length, KernelFileStats.Bound(new byte[60], count).Length);
}
