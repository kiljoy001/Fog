using NinePSharp.Fog.Commands;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// sleep.c's reading of its argument: seconds as strtol reads them, then up to three places of a
// fraction, as milliseconds, with what is past a long's reach slept in pieces.
public sealed class SleepTests
{
    [Theory]
    [InlineData("", new long[0])]
    [InlineData("0", new long[0])]
    [InlineData("abc", new long[0])]
    [InlineData("-1", new long[0])]
    [InlineData("1", new long[] { 1000 })]
    [InlineData("5x", new long[] { 5000 })]
    [InlineData("010", new long[] { 8000 })]
    [InlineData("0x10", new long[] { 16000 })]
    [InlineData("1.5", new long[] { 1500 })]
    [InlineData("1.25", new long[] { 1250 })]
    [InlineData("1.125", new long[] { 1125 })]
    [InlineData("1.12345", new long[] { 1123 })]
    [InlineData(".5", new long[] { 500 })]
    [InlineData("0.05", new long[] { 50 })]
    [InlineData("1.0", new long[] { 1000 })]
    [InlineData("1.-5", new long[] { 1000 })]
    [InlineData("1. 5", new long[] { 1050 })]
    [InlineData("1.  1234", new long[] { 1001 })]
    [InlineData("4294966", new long[] { 2147482000, 2147482000, 2000 })]
    public void SleepSleepsAsSleepCReadsItsArgument(string argument, long[] sleeps)
    {
        Assert.Equal(sleeps, Sleep.Sleeps(argument));
    }
}
