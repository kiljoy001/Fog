using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class LibcTests
{
    // The formats fltfmt.tsv gives each value in, after its bits, as 9front's print gave them.
    private static readonly string[] Floats =
    [
        "%g", "%.6g", "%.0g", "%.1g", "%.3g", "%.10g", "%.15g", "%.16g", "%.17g", "%.20g",
        "%e", "%.0e", "%.3e", "%.16e", "%E", "%G", "%f", "%.0f", "%.2f", "%.10f", "%.20f", "%.35f",
        "%#g", "%#.0f", "%#.0e", "%+g", "% g", "%+.3f", "%10.3g", "%-10.2f|", "%08.2f", "%#.3g",
    ];

    // Each format with its arguments and the bytes 9front's snprint gave, in hex, with errstr set
    // to "an error".
    public static TheoryData<string, Libc.FmtArg[], string> Prints { get; } = new()
    {
        { "%d", [Libc.FmtArg.Of(0L)], "30" },
        { "%d", [Libc.FmtArg.Of(42L)], "3432" },
        { "%d", [Libc.FmtArg.Of(-42L)], "2d3432" },
        { "%d", [Libc.FmtArg.Of(2147483647L)], "32313437343833363437" },
        { "%d", [Libc.FmtArg.Of(-2147483648L)], "2d32313437343833363438" },
        { "%5d|", [Libc.FmtArg.Of(42L)], "20202034327c" },
        { "%-5d|", [Libc.FmtArg.Of(42L)], "34322020207c" },
        { "%05d", [Libc.FmtArg.Of(42L)], "3030303432" },
        { "%05d", [Libc.FmtArg.Of(-42L)], "2d30303432" },
        { "%+d", [Libc.FmtArg.Of(42L)], "2b3432" },
        { "% d", [Libc.FmtArg.Of(42L)], "203432" },
        { "%+ d", [Libc.FmtArg.Of(42L)], "2b3432" },
        { "%x", [Libc.FmtArg.Of(255L)], "6666" },
        { "%X", [Libc.FmtArg.Of(255L)], "4646" },
        { "%#x", [Libc.FmtArg.Of(255L)], "30786666" },
        { "%#X", [Libc.FmtArg.Of(255L)], "30584646" },
        { "%#x", [Libc.FmtArg.Of(0L)], "307830" },
        { "%#o", [Libc.FmtArg.Of(8L)], "303130" },
        { "%#o", [Libc.FmtArg.Of(0L)], "30" },
        { "%o", [Libc.FmtArg.Of(8L)], "3130" },
        { "%b", [Libc.FmtArg.Of(5L)], "313031" },
        { "%#b", [Libc.FmtArg.Of(5L)], "313031" },
        { "%,d", [Libc.FmtArg.Of(1234567L)], "312c3233342c353637" },
        { "%,d", [Libc.FmtArg.Of(-1234567L)], "2d312c3233342c353637" },
        { "%,x", [Libc.FmtArg.Of(305419896L)], "31322c3334352c363738" },
        { "%ud", [Libc.FmtArg.Of(-1L)], "34323934393637323935" },
        { "%ux", [Libc.FmtArg.Of(-1L)], "6666666666666666" },
        { "%hd", [Libc.FmtArg.Of(65537L)], "31" },
        { "%hd", [Libc.FmtArg.Of(-1L)], "2d31" },
        { "%hhd", [Libc.FmtArg.Of(257L)], "31" },
        { "%lhd", [Libc.FmtArg.Of(65537L)], "3635353337" },
        { "%hhd", [Libc.FmtArg.Of(255L)], "2d31" },
        { "%hhud", [Libc.FmtArg.Of(255L)], "323535" },
        { "%hud", [Libc.FmtArg.Of(-1L)], "3635353335" },
        { "%.5d", [Libc.FmtArg.Of(42L)], "3030303432" },
        { "%8.5d|", [Libc.FmtArg.Of(42L)], "20202030303034327c" },
        { "%-8.5d|", [Libc.FmtArg.Of(42L)], "30303034322020207c" },
        { "%08.5d|", [Libc.FmtArg.Of(42L)], "20202030303034327c" },
        { "%#08x", [Libc.FmtArg.Of(255L)], "3078303030306666" },
        { "%-08d|", [Libc.FmtArg.Of(42L)], "34322020202020207c" },
        { "%.0d|", [Libc.FmtArg.Of(0L)], "307c" },
        { "%+05d", [Libc.FmtArg.Of(42L)], "2b30303432" },
        { "%c", [Libc.FmtArg.Of(65L)], "41" },
        { "%5c|", [Libc.FmtArg.Of(65L)], "20202020417c" },
        { "%-5c|", [Libc.FmtArg.Of(65L)], "41202020207c" },
        { "%c|", [Libc.FmtArg.Of(200L)], "7c" },
        { "%C", [Libc.FmtArg.Of(9786L)], "e298ba" },
        { "%C", [Libc.FmtArg.Of(1114112L)], "efbfbd" },
        { "%5C|", [Libc.FmtArg.Of(9786L)], "20202020e298ba7c" },
        { "%-3C|", [Libc.FmtArg.Of(9786L)], "e298ba20207c" },
        { "%ld", [Libc.FmtArg.Of(-5L)], "2d35" },
        { "%lux", [Libc.FmtArg.Of(-1L)], "6666666666666666" },
        { "%lx", [Libc.FmtArg.Of(-1L)], "2d31" },
        { "%lld", [Libc.FmtArg.Of(9223372036854775807L)], "39323233333732303336383534373735383037" },
        { "%lld", [Libc.FmtArg.Of(long.MinValue)], "2d39323233333732303336383534373735383038" },
        { "%lld", [Libc.FmtArg.Of(-5L)], "2d35" },
        { "%llx", [Libc.FmtArg.Of(-1L)], "2d31" },
        { "%llux", [Libc.FmtArg.Of(-1L)], "66666666666666666666666666666666" },
        { "%,lld", [Libc.FmtArg.Of(1234567890123L)], "312c3233342c3536372c3839302c313233" },
        { "%#llo", [Libc.FmtArg.Of(8L)], "303130" },
        { "%zd", [Libc.FmtArg.Of(-5L)], "2d35" },
        { "%p", [Libc.FmtArg.Of(3735928559L)], "6465616462656566" },
        { "%#p", [Libc.FmtArg.Of(3735928559L)], "30786465616462656566" },
        { "%20p|", [Libc.FmtArg.Of(3735928559L)], "20202020202020202020202064656164626565667c" },
        { "%g", [Libc.FmtArg.Of(1.5d)], "312e35" },
        { "%5.1f|", [Libc.FmtArg.Of(3.14159d)], "2020332e317c" },
        { "%-8.3e|", [Libc.FmtArg.Of(1234.5d)], "312e323334652b30337c" },
        { "%08.3f", [Libc.FmtArg.Of(-1.5d)], "20202d312e353030" },
        { "%+.2f", [Libc.FmtArg.Of(2.0d)], "2b322e3030" },
        { "% g", [Libc.FmtArg.Of(2.0d)], "2032" },
        { "%#.0f", [Libc.FmtArg.Of(3.0d)], "332e" },
        { "%.3G", [Libc.FmtArg.Of(1e-10d)], "31452d3130" },
        { "%E", [Libc.FmtArg.Of(12345.678d)], "312e323334353638452b3034" },
        { "%10.4g|", [Libc.FmtArg.Of(0.000123456d)], "20302e303030313233357c" },
        { "%s", [Libc.FmtArg.Of("hello")], "68656c6c6f" },
        { "%10s|", [Libc.FmtArg.Of("héllo")], "202020202068c3a96c6c6f7c" },
        { "%-10s|", [Libc.FmtArg.Of("héllo")], "68c3a96c6c6f20202020207c" },
        { "%.2s|", [Libc.FmtArg.Of("héllo")], "68c3a97c" },
        { "%.3s|", [Libc.FmtArg.Of("héllo")], "68c3a96c7c" },
        { "%s", [default(Libc.FmtArg)], "3c6e696c3e" },
        { "%5.1s|", [Libc.FmtArg.Of("xyz")], "20202020787c" },
        { "%.0s|", [Libc.FmtArg.Of("xyz")], "7c" },
        { "%7s|", [default(Libc.FmtArg)], "20203c6e696c3e7c" },
        { "%.2s|", [default(Libc.FmtArg)], "3c6e7c" },
        { "%S", [Libc.FmtArg.Of("héllo")], "68c3a96c6c6f" },
        { "%8S|", [Libc.FmtArg.Of("héllo")], "20202068c3a96c6c6f7c" },
        { "%.2S|", [Libc.FmtArg.Of("héllo")], "68c3a97c" },
        { "%S", [default(Libc.FmtArg)], "3c6e696c3e" },
        { "%%", [], "25" },
        { "%5%|", [], "20202020257c" },
        { "%-5%|", [], "25202020207c" },
        { "%q", [], "257125" },
        { "%5q|", [], "20202571257c" },
        { "%-5q|", [], "25712520207c" },
        { "%y", [], "257925" },
        { "%é|", [], "25c3a925007c" },
        { "%r", [], "616e206572726f72" },
        { "%10r|", [], "2020616e206572726f727c" },
        { "%.3r|", [], "616e207c" },
        { "abc", [], "616263" },
        { "abc%", [], "616263" },
        { "x%5", [], "78" },
        { "a%%b%%c", [], "6125622563" },
        { "hé%%", [], "68c3a925" },
        { "%*d|", [Libc.FmtArg.Of(5L), Libc.FmtArg.Of(42L)], "20202034327c" },
        { "%*d|", [Libc.FmtArg.Of(-5L), Libc.FmtArg.Of(42L)], "34322020207c" },
        { "%-*d|", [Libc.FmtArg.Of(5L), Libc.FmtArg.Of(42L)], "34322020207c" },
        { "%.*d|", [Libc.FmtArg.Of(4L), Libc.FmtArg.Of(42L)], "303034327c" },
        { "%.*d|", [Libc.FmtArg.Of(-4L), Libc.FmtArg.Of(42L)], "34327c" },
        { "%.*f", [Libc.FmtArg.Of(2L), Libc.FmtArg.Of(3.14159d)], "332e3134" },
        { "%.*f", [Libc.FmtArg.Of(-1L), Libc.FmtArg.Of(3.14159d)], "332e313431353930" },
        { "%*.*f|", [Libc.FmtArg.Of(8L), Libc.FmtArg.Of(2L), Libc.FmtArg.Of(3.14159d)], "20202020332e31347c" },
        { "%*s|", [Libc.FmtArg.Of(6L), Libc.FmtArg.Of("ab")], "2020202061627c" },
        { "%.*s|", [Libc.FmtArg.Of(1L), Libc.FmtArg.Of("ab")], "617c" },
        { "%--5d|", [Libc.FmtArg.Of(42L)], "34322020207c" },
        { "%zd", [Libc.FmtArg.Of(4294967301L)], "34323934393637333031" },
        { "%up", [Libc.FmtArg.Of(-1L)], "66666666666666666666666666666666" },
        { "%.80d|", [Libc.FmtArg.Of(1L)], "3030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030317c" },
        { "%080d|", [Libc.FmtArg.Of(1L)], "3030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030317c" },
        { "%.10s|", [Libc.FmtArg.Of("abc")], "6162637c" },
    };

    public static TheoryData<string> Values()
    {
        var values = new TheoryData<string>();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "fltfmt.tsv")))
        {
            values.Add(line);
        }

        return values;
    }

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
    [InlineData(0, "Thu Jan  1 00:00:00 GMT 1970\n")]
    [InlineData(1700000000, "Tue Nov 14 22:13:20 GMT 2023\n")]
    public void CtimeIsAsctimesLineInGmt(long seconds, string line) => Assert.Equal(line, Libc.Ctime(seconds));

    [Fact]
    public async Task GetwdNamesDotAndClosesWhatItOpened()
    {
        FogKernel kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "eve");
        Process init = await kernel.BootAsync();
        await init.ChdirAsync("/tmp");

        Assert.Equal("/tmp", await Libc.GetwdAsync(init));
        Assert.Equal(0, await init.OpenAsync("/dev/null", NinePConstants.OREAD));
    }

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

    // fltfmt.c's %e, %f and %g, each value as 9front printed it in each format: zeros, negative
    // zero, NaN and Inf, numbers that are halfway at the precision, past 17 digits, and as small as
    // a denormal or as big as a double goes. The most negative double is left out: its %.35f is a
    // byte longer than _floatfmt's buffer, which 9front overruns.
    [Theory]
    [MemberData(nameof(Values))]
    public void FloatfmtPrintsAsFltfmtDoes(string line)
    {
        string[] fields = line.Split('\t');
        double v = BitConverter.Int64BitsToDouble((long)ulong.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        Assert.Equal(fields[1..], Floats.Select(f => Encoding.UTF8.GetString(Libc.Sprint(f, [Libc.FmtArg.Of(v)]))));
    }

    // pow10, as 9front gives it: from its table to 1e159, and past it the product of two halves,
    // or for a negative power the reciprocal of either.
    [Theory]
    [InlineData(-200, "16687e92154ef7ac")]
    [InlineData(-161, "1e81fee341fc585d")]
    [InlineData(-160, "1eb67e9c127b6e74")]
    [InlineData(-159, "1eec1e43171a4a12")]
    [InlineData(-46, "366244ce242c5561")]
    [InlineData(-1, "3fb999999999999a")]
    [InlineData(0, "3ff0000000000000")]
    [InlineData(1, "4024000000000000")]
    [InlineData(22, "4480f0cf064dd592")]
    [InlineData(23, "44b52d02c7e14af6")]
    [InlineData(159, "60f2357684599702")]
    [InlineData(160, "6126c2d4256ffcc3")]
    [InlineData(161, "615c73892ecbfbf3")]
    [InlineData(200, "6974e718d7d7625a")]
    [InlineData(308, "7fe1ccf385ebc8a0")]
    public void Pow10IsPlan9s(int n, string bits)
    {
        Assert.Equal(bits, BitConverter.DoubleToInt64Bits(Libc.Pow10(n)).ToString("x16", CultureInfo.InvariantCulture));
    }

    // xadd and xsub: a carry or borrow runs up the digits from the one given, and past the first
    // makes it 1 or 9; xadd adds nothing at a digit outside the 17.
    [Theory]
    [InlineData("12345678901234567", 16, 1, "12345678901234568", false)]
    [InlineData("12345678901234569", 16, 1, "12345678901234570", false)]
    [InlineData("19999999999999999", 16, 1, "20000000000000000", false)]
    [InlineData("99999999999999999", 16, 1, "10000000000000000", true)]
    [InlineData("12345678901234567", 3, 5, "12395678901234567", false)]
    [InlineData("12345678901234567", 0, 9, "12345678901234567", true)]
    [InlineData("12345678901234567", 17, 1, "12345678901234567", false)]
    [InlineData("12345678901234567", -1, 1, "12345678901234567", false)]
    public void XaddCarriesUpTheDigits(string digits, int n, int v, string after, bool carried)
    {
        char[] a = digits.ToCharArray();
        Assert.Equal((carried, after), (Libc.Xadd(a, n, v), new string(a)));
    }

    [Theory]
    [InlineData("12345678901234567", 16, 1, "12345678901234566", false)]
    [InlineData("12345678901234561", 16, 1, "12345678901234560", false)]
    [InlineData("12345678901234560", 16, 1, "12345678901234559", false)]
    [InlineData("10000000000000000", 16, 1, "09999999999999999", false)]
    [InlineData("00000000000000000", 16, 1, "99999999999999999", true)]
    public void XsubBorrowsDownTheDigits(string digits, int n, int v, string after, bool borrowed)
    {
        char[] a = digits.ToCharArray();
        Assert.Equal((borrowed, after), (Libc.Xsub(a, n, v), new string(a)));
    }

    // strtod.c's reading of each number, as 9front read it: the bits of the double it gave, and
    // where it stopped.
    [Theory]
    [InlineData("", "0000000000000000", 0)]
    [InlineData(" ", "0000000000000000", 0)]
    [InlineData("  12", "4028000000000000", 4)]
    [InlineData("+5", "4014000000000000", 2)]
    [InlineData("-5", "c014000000000000", 2)]
    [InlineData("--5", "8000000000000000", 0)]
    [InlineData("-+5", "8000000000000000", 0)]
    [InlineData("5-", "4014000000000000", 1)]
    [InlineData("5e3-", "3f747ae147ae147b", 3)]
    [InlineData("5e-3", "3f747ae147ae147b", 4)]
    [InlineData("5e", "4014000000000000", 1)]
    [InlineData("5e+", "4014000000000000", 1)]
    [InlineData("5e-", "4014000000000000", 1)]
    [InlineData(".5", "3fe0000000000000", 2)]
    [InlineData("5.", "4014000000000000", 2)]
    [InlineData(".", "0000000000000000", 0)]
    [InlineData("-.", "8000000000000000", 0)]
    [InlineData("0", "0000000000000000", 1)]
    [InlineData("-0", "8000000000000000", 2)]
    [InlineData("00100", "4059000000000000", 5)]
    [InlineData("0.001", "3f50624dd2f1a9fc", 5)]
    [InlineData("1e309", "7ff0000000000000", 5)]
    [InlineData("-1e309", "fff0000000000000", 6)]
    [InlineData("1e-330", "0000000000000000", 6)]
    [InlineData("-1e-330", "8000000000000000", 7)]
    [InlineData("2.5e-324", "0000000000000001", 8)]
    [InlineData("1e-327", "0000000000000000", 6)]
    [InlineData("1e-326", "0000000000000000", 6)]
    [InlineData("nan", "7ff0000000000001", 3)]
    [InlineData("NaN", "7ff0000000000001", 3)]
    [InlineData("-nan", "fff0000000000001", 4)]
    [InlineData("+NAN", "7ff0000000000001", 4)]
    [InlineData("inf", "7ff0000000000000", 3)]
    [InlineData("-INF", "fff0000000000000", 4)]
    [InlineData("infinity", "7ff0000000000000", 8)]
    [InlineData("Infinityx", "7ff0000000000000", 8)]
    [InlineData("infx", "7ff0000000000000", 3)]
    [InlineData("in", "0000000000000000", 0)]
    [InlineData("na", "0000000000000000", 0)]
    [InlineData("0x10", "0000000000000000", 1)]
    [InlineData("1,5", "3ff0000000000000", 1)]
    [InlineData("3.14159abc", "400921f9f01b866e", 7)]
    [InlineData("\t\n 7", "401c000000000000", 4)]
    [InlineData("1e2147483648", "0000000000000000", 12)]
    [InlineData("1e4294967306", "4202a05f20000000", 12)]
    [InlineData("12345678901234567890123456789", "45c3f20d99235f65", 29)]
    [InlineData("0.1", "3fb999999999999a", 3)]
    [InlineData("9007199254740993", "4340000000000000", 16)]
    [InlineData("1.7976931348623158e308", "7fefffffffffffff", 22)]
    [InlineData("1.7976931348623159e308", "7ff0000000000000", 22)]
    [InlineData("4.9406564584124654e-324", "0000000000000001", 23)]
    [InlineData("2.4703282292062328e-324", "0000000000000001", 23)]
    [InlineData("2.4703282292062327e-324", "0000000000000000", 23)]
    [InlineData("1.5.3", "402e000000000000", 3)]
    [InlineData("1e5e3", "40f86a0000000000", 3)]
    [InlineData("0.000", "0000000000000000", 5)]
    [InlineData("-0.0e5", "8000000000000000", 6)]
    [InlineData(" -", "8000000000000000", 0)]
    [InlineData("+", "0000000000000000", 0)]
    [InlineData("e5", "0000000000000000", 0)]
    [InlineData("1.e5", "40f86a0000000000", 4)]
    [InlineData("0001.2300e-0002", "3f8930be0ded288d", 15)]
    [InlineData("123456789012345678e-18", "3fbf9add3746f65f", 22)]
    [InlineData("5 e3", "4014000000000000", 1)]
    [InlineData("7.", "401c000000000000", 2)]
    [InlineData("-.e1", "8000000000000000", 0)]
    [InlineData("5e-3-", "3f747ae147ae147b", 4)]
    public void StrtodReadsAsPlan9sLibcDoes(string s, string bits, int end)
    {
        Assert.Equal((bits, end), (BitConverter.DoubleToInt64Bits(Libc.Strtod(s, out int stop)).ToString("x16", CultureInfo.InvariantCulture), stop));
    }

    // strtod keeps 1450 digits: a tie broken only past them stays a tie, and rounds to even.
    [Fact]
    public void StrtodReadsNoMoreThan1450Digits()
    {
        string past = "9007199254740993." + new string('0', 1434) + "1";
        string within = "9007199254740993." + new string('0', 1433) + "1";
        Assert.Equal((0x4340000000000000L, past.Length), (BitConverter.DoubleToInt64Bits(Libc.Strtod(past, out int end)), end));
        Assert.Equal((0x4340000000000001L, within.Length), (BitConverter.DoubleToInt64Bits(Libc.Strtod(within, out end)), end));
    }

    // A slot past the arguments reads as zeros; a string is read as far as its NUL; a number read
    // as a string, or %n's pointer, is no string to read or write.
    [Fact]
    public void SprintReadsItsSlotsAsTheyAre()
    {
        Assert.Equal("1 0 <nil>", Encoding.UTF8.GetString(Libc.Sprint("%d %d %s", [Libc.FmtArg.Of(1L)])));
        Assert.Equal("ab|ab|", Encoding.UTF8.GetString(Libc.Sprint("%s|%.3s|", [Libc.FmtArg.Of("ab\0cd"), Libc.FmtArg.Of("ab\0cd")])));
        Assert.Equal("a number where the format reads a string", Assert.Throws<ArgumentException>(() => Libc.Sprint("%s", [Libc.FmtArg.Of(5L)])).Message);
        Assert.Equal("%n writes where the slot points, which is not a pointer", Assert.Throws<ArgumentException>(() => Libc.Sprint("%n", [Libc.FmtArg.Of(0L)])).Message);
    }

    // dofmt's verbs and flags, each as 9front printed it: integers of each size, base and flag,
    // characters, runes and strings to their widths and precisions, %r, %p, '*' and an unknown verb.
    [Theory]
    [MemberData(nameof(Prints))]
    public void SprintPrintsAsDofmtDoes(string format, Libc.FmtArg[] args, string hex)
    {
        Assert.Equal(hex, Convert.ToHexString(Libc.Sprint(format, args, "an error")).ToLowerInvariant());
    }

    // %s of bytes as they are: as far as a NUL, a byte that begins no rune the error rune, and
    // where fullrune says the bytes left are too few for a rune, no more.
    [Fact]
    public void SprintTakesStringsOfBytes()
    {
        Assert.Equal(string.Empty, Convert.ToHexString(Libc.Sprint("%s"u8.ToArray(), [Libc.FmtArg.Of(new byte[] { 0, 0x41 })])));
        Assert.Equal("EFBFBD41424344", Convert.ToHexString(Libc.Sprint("%s"u8.ToArray(), [Libc.FmtArg.Of(new byte[] { 0xff, 0x41, 0x42, 0x43, 0x44 })])));
        Assert.Equal(string.Empty, Convert.ToHexString(Libc.Sprint("%s"u8.ToArray(), [Libc.FmtArg.Of(new byte[] { 0xff, 0x41 })])));
        Assert.Equal("FF25", Convert.ToHexString(Libc.Sprint(new byte[] { 0xff, (byte)'%', (byte)'%' }, [])));
    }

    // %q and %Q, as quotefmtinstall installs them, and as no verb without it, as awk's printf
    // prints them on 9front.
    [Fact]
    public void QuoteVerbsQuoteAsQuotefmtinstallsDo()
    {
        static string Q(string format, params string[] args)
            => System.Text.Encoding.UTF8.GetString(Libc.Sprint(System.Text.Encoding.UTF8.GetBytes(format), [.. args.Select(Libc.FmtArg.Of)], quotefmt: true));

        Assert.Equal("   ab|'a b' |a|''|'x''y'|'a '|  é|", Q("%5q|%-6q|%.1q|%q|%q|%.2q|%3q|", "ab", "a b", "abc", string.Empty, "x'y", "a b", "é"));
        Assert.Equal("'ab'|'a\tb'|<nil>|' é'", Q("%#q|%q|%q|%Q", "ab", "a\tb", null!, " é"));
        Assert.Equal("%q%", System.Text.Encoding.UTF8.GetString(Libc.Sprint("%q"u8.ToArray(), [Libc.FmtArg.Of("a b")])));
    }

    // %q of strings of bytes against 9front's: each of quote.tsv's lines the bytes quotefmtinstall's
    // %q gave of a format and a string, among them widths, '-', '#', precisions, quotes, blanks,
    // runes past ASCII, a byte that begins none and runes cut short.
    [Fact]
    public void QuoteFormatsBytesAs9frontsDoes()
    {
        string[] formats = ["%q", "%5q", "%-5q", "%12q", "%-12q", "%.0q", "%.1q", "%.2q", "%.3q", "%#q", "%#5q", "%#-6q", "%#.2q", "%#.0q", "%3.1q", "%-8.3q", "%12.4q"];
        byte[][] strings =
        [
            [], "ab"u8.ToArray(), "a b"u8.ToArray(), "it's"u8.ToArray(), "x'y'z"u8.ToArray(), [0xc3, 0xa9], "héllo wörld"u8.ToArray(), " é"u8.ToArray(),
            "a\tb"u8.ToArray(), [0xff], [0x61, 0xc3], [0xc3], [0xe2, 0x82], [0x61, 0x62, 0xe2, 0x82], [0x7f], [0x61, 0x01, 0x62], "'''"u8.ToArray(), "abcdefghij"u8.ToArray(),
        ];
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Data", "quote.tsv")))
        {
            string[] f = line.Split(' ');
            byte[] got = Libc.Sprint(System.Text.Encoding.ASCII.GetBytes(formats[int.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture)]), [Libc.FmtArg.Of(strings[int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture)])], quotefmt: true);
            Assert.True(Convert.ToHexStringLower(got) == f[2], line);
        }
    }

    // A string of bytes ends at its first NUL, as C's does, quoted or not.
    [Fact]
    public void AStringOfBytesEndsAtItsNul()
    {
        static string S(string format, byte[] s) => System.Text.Encoding.UTF8.GetString(Libc.Sprint(System.Text.Encoding.UTF8.GetBytes(format), [Libc.FmtArg.Of(s)], quotefmt: true));

        Assert.Equal("ab|'a b'||''|''", S("%s|", [0x61, 0x62, 0, 0x63]) + S("%q|", [0x61, 0x20, 0x62, 0, 0x63]) + S("%s|", [0, 0x61]) + S("%q|", [0, 0x61]) + S("%.1q", [0, 0x61]));
    }
}
