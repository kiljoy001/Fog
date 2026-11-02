using NinePSharp.Fog.Commands;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// diff's shellsort: lines a+1 to a+n put in order of their hash and then their number, the lines
// around them left where they are.
public sealed class DiffSortTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(25)]
    [InlineData(64)]
    [InlineData(100)]
    public void SortOrdersByValueThenSerial(int n)
    {
        var random = new Random(n);
        for (int round = 0; round < 20; round++)
        {
            var f = new Diff.Line[n + 3];
            f[0] = new Diff.Line(-1, -1);
            f[n + 1] = new Diff.Line(-2, -2);
            f[n + 2] = new Diff.Line(-3, -3);
            for (int i = 1; i <= n; i++)
            {
                f[i] = new Diff.Line(i, random.Next(5));
            }

            Diff.Line[] unsorted = [.. f];
            Diff.Sort(f, 0, n);
            Assert.Equal(unsorted[1..(n + 1)].OrderBy(l => l.Value).ThenBy(l => l.Serial), f[1..(n + 1)]);
            Assert.Equal((unsorted[0], unsorted[n + 1], unsorted[n + 2]), (f[0], f[n + 1], f[n + 2]));
        }
    }
}
