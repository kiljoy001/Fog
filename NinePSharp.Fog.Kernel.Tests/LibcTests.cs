using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class LibcTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "''")]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("tab\there", "'tab\there'")]
    [InlineData("x=y", "'x=y'")]
    [InlineData("ünï", "ünï")]
    [InlineData("cut\0off", "cut")]
    [InlineData("\0", "''")]
    [InlineData("~/a.b-c_d!", "~/a.b-c_d!")]
    public void QuoteQuotesAsRcNeedsIt(string s, string quoted) => Assert.Equal(quoted, Libc.Quote(s));

    [Theory]
    [InlineData('`')]
    [InlineData('^')]
    [InlineData('#')]
    [InlineData('*')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('=')]
    [InlineData('|')]
    [InlineData('\\')]
    [InlineData('?')]
    [InlineData('$')]
    [InlineData('{')]
    [InlineData('}')]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('&')]
    [InlineData(';')]
    [InlineData(' ')]
    [InlineData('\x7f')]
    public void EachOfRcsSpecialCharactersIsQuoted(char c) => Assert.Equal(c == '\x7f' ? "\x7f" : $"'{c}'", Libc.Quote(c.ToString()));

    [Theory]
    [InlineData("", 10, 0, 0)]
    [InlineData(" \t\n\f\r\v42", 10, 42, 8)]
    [InlineData("+7", 10, 7, 2)]
    [InlineData("-7", 10, -7, 2)]
    [InlineData("-", 10, 0, 0)]
    [InlineData("+-1", 10, 0, 0)]
    [InlineData("7a", 10, 7, 1)]
    [InlineData("a", 10, 0, 0)]
    [InlineData("017", 0, 15, 3)]
    [InlineData("0x1f", 0, 31, 4)]
    [InlineData("0X1F", 0, 31, 4)]
    [InlineData("0x", 0, 0, 0)]
    [InlineData("0", 0, 0, 1)]
    [InlineData("09", 0, 0, 1)]
    [InlineData("12", 0, 12, 2)]
    [InlineData("0x1f", 16, 31, 4)]
    [InlineData("0X1f", 16, 31, 4)]
    [InlineData("1f", 16, 31, 2)]
    [InlineData("0z", 16, 0, 1)]
    [InlineData("zZ", 36, 1295, 2)]
    [InlineData("19", 8, 1, 1)]
    [InlineData("5", 37, 0, 0)]
    [InlineData("5", -1, 0, 0)]
    [InlineData("001", 1, 0, 2)]
    [InlineData("2147483647", 10, int.MaxValue, 10)]
    [InlineData("2147483648", 10, int.MaxValue, 10)]
    [InlineData("2147483650", 10, int.MaxValue, 10)]
    [InlineData("-2147483648", 10, int.MinValue, 11)]
    [InlineData("-2147483649", 10, int.MinValue, 11)]
    [InlineData("99999999999", 10, int.MaxValue, 11)]
    [InlineData("2147483640", 10, 2147483640, 10)]
    [InlineData("0755281220e0878", 16, int.MaxValue, 15)]
    [InlineData("a", 16, 10, 1)]
    [InlineData("A", 16, 10, 1)]
    [InlineData("1x5", 16, 1, 1)]
    [InlineData("0x5", 10, 0, 1)]
    public void StrtolReadsAsPlan9sLibcDoes(string s, int radix, int value, int end)
    {
        Assert.Equal((value, end), (Libc.Strtol(s, radix, out int stop), stop));
    }

    [Theory]
    [InlineData("/")]
    [InlineData(":")]
    [InlineData("@")]
    [InlineData("[")]
    [InlineData("`")]
    [InlineData("{")]
    public void ACharacterBetweenTheDigitsAndLettersIsNoDigit(string s)
    {
        Assert.Equal((0, 0), (Libc.Strtol(s, 36, out int stop), stop));
    }

    [Theory]
    [InlineData("4294967296", 10, 4294967296, 10)]
    [InlineData("9223372036854775807", 10, long.MaxValue, 19)]
    [InlineData("9223372036854775808", 10, long.MaxValue, 19)]
    [InlineData("-9223372036854775809", 10, long.MinValue, 20)]
    public void StrtollReadsAVlong(string s, int radix, long value, int end)
    {
        Assert.Equal((value, end), (Libc.Strtoll(s, radix, out int stop), stop));
    }

    [Theory]
    [InlineData("", 10, 0u, 0)]
    [InlineData(" \t\n\f\r\v+5", 10, 5u, 8)]
    [InlineData("-1", 10, uint.MaxValue, 2)]
    [InlineData("-", 10, 0u, 0)]
    [InlineData("17", 0, 17u, 2)]
    [InlineData("017", 0, 15u, 3)]
    [InlineData("0x1f", 0, 31u, 4)]
    [InlineData("0X1F", 0, 31u, 4)]
    [InlineData("0xg", 0, 0u, 1)]
    [InlineData("0x", 16, 0u, 1)]
    [InlineData("0x1f", 16, 31u, 4)]
    [InlineData("Zz", 36, 1295u, 2)]
    [InlineData("5", 1, 0u, 0)]
    [InlineData("5", 37, 0u, 0)]
    [InlineData("101", 2, 5u, 3)]
    [InlineData("4294967295", 10, uint.MaxValue, 10)]
    [InlineData("4294967296", 10, uint.MaxValue, 10)]
    [InlineData("42949672950", 10, uint.MaxValue, 11)]
    [InlineData("4294967290", 10, 4294967290u, 10)]
    [InlineData("9999999999", 10, uint.MaxValue, 10)]
    [InlineData("999999999999", 10, uint.MaxValue, 12)]
    [InlineData("2x5", 16, 2u, 1)]
    [InlineData("0x5", 10, 0u, 1)]
    public void StrtoulReadsAsPlan9sLibcDoes(string s, int radix, uint value, int end)
    {
        Assert.Equal((value, end), (Libc.Strtoul(s, radix, out int stop), stop));
    }
}
