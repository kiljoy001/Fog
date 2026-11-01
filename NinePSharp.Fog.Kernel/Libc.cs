using System.Globalization;
using System.Numerics;
using System.Text;
using NinePSharp.Constants;

namespace NinePSharp.Fog.Kernel;

// Plan 9 libc's strtol, strtoll and strtoul, whose longs are 32 bits: the number at the start of s,
// and in end where it stops, or 0 when no digit was read; %q as quotefmtinstall gives it; %e, %f
// and %g as fltfmt.c gives them; and getwd.
public static class Libc
{
    private const string RcSpecial = "`^#*[]=|\\?${}()'<>&;";
    private const int FDigit = 30;
    private const int FDeflt = 6;
    private const int Nsignif = 17;

    // pow10's table: 1e0 to 1e159, each as the compiler rounds it.
    private static readonly double[] Tab = [.. Enumerable.Range(0, 160).Select(n => double.Parse($"1e{n}", CultureInfo.InvariantCulture))];

    // %q with needsrcquote: quoted in rc's way when it is empty or holds a space, a control
    // character or one of rc's, its quotes doubled; it ends at a NUL.
    public static string Quote(string s)
    {
        int nul = s.IndexOf('\0', StringComparison.Ordinal);
        s = nul < 0 ? s : s[..nul];
        return s.Length == 0 || s.Any(c => c <= ' ' || RcSpecial.Contains(c, StringComparison.Ordinal))
            ? $"'{s.Replace("'", "''", StringComparison.Ordinal)}'"
            : s;
    }

    // getwd: the name of dot, as fd2path gives it.
    public static async Task<string> GetwdAsync(Process process)
    {
        // Stryker disable once Bitwise : the descriptor is closed before getwd returns, so no exec sees it
        int fd = await process.OpenAsync(".", NinePConstants.OREAD | NinePConstants.OCEXEC);
        try
        {
            return await process.Fd2PathAsync(fd);
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    // _floatfmt: f as the verb e, f or g gives it, or E or G with a capital E, to the precision
    // given, 6 by default and 30 at most; with flags of '+', ' ', '#' and '-', and the width, which
    // is padded with spaces, as _floatfmt keeps no flag but the width and '-'.
    public static string Floatfmt(double f, char verb, int? prec = null, string flags = "", int width = 0)
    {
        string s = Xdtoa(f, verb, Math.Min(prec ?? FDeflt, FDigit), flags);
        return flags.Contains('-', StringComparison.Ordinal) ? s.PadRight(width) : s.PadLeft(width);
    }

    // ctime: asctime's "WW MMM _D hh:mm:ss ZZZ YYYY\n" for the time, in GMT, Fog's only zone.
    public static string Ctime(long seconds)
    {
        DateTime t = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{t:ddd MMM} {t.Day,2} {t:HH:mm:ss} GMT {t.Year}\n");
    }

    public static int Strtol(string s, int radix, out int end) => Signed<int>(s, radix, out end);

    public static long Strtoll(string s, int radix, out int end) => Signed<long>(s, radix, out end);

    // strtoul skips a 0x only before a hex digit, and gives the largest for an overflow.
    public static uint Strtoul(string s, int radix, out int end)
    {
        (int p, bool neg) = Sign(s);
        if (radix == 0)
        {
            radix = At(s, p) != '0' ? 10 : At(s, p + 1) is 'x' or 'X' ? 16 : 8;
        }

        uint n = 0;
        int start = p;
        bool ovfl = false;
        if (radix is >= 2 and <= 36)
        {
            p = HexDigits(s, p, radix);
            (n, ovfl) = Digits(s, ref p, radix, uint.MaxValue);
        }

        end = p == start ? 0 : p;
        return ovfl ? uint.MaxValue : neg ? unchecked(0u - n) : n;
    }

    private static T Signed<T>(string s, int radix, out int end)
        where T : IBinaryInteger<T>, IMinMaxValue<T>
    {
        (int p, bool neg) = Sign(s);
        radix = Radix(s, ref p, radix);
        T n = T.Zero;
        int start = p;
        bool ovfl = false;
        if (radix is >= 1 and <= 36)
        {
            (n, ovfl) = Digits(s, ref p, radix, T.MaxValue);
        }

        end = p == start ? 0 : p;
        return ovfl ? (neg ? T.MinValue : T.MaxValue) : neg ? -n : n;
    }

    // Past a 0x that a hex digit follows, for base 16.
    private static int HexDigits(string s, int p, int radix)
        => radix == 16 && At(s, p) == '0' && At(s, p + 1) is 'x' or 'X' && Digit(At(s, p + 2)) < 16 ? p + 2 : p;

    // Where the number starts, past blanks and a sign, and whether the sign is -.
    private static (int P, bool Neg) Sign(string s)
    {
        int p = Spaces(s);
        bool neg = At(s, p) == '-';
        return (At(s, p) is '-' or '+' ? p + 1 : p, neg);
    }

    // strtol's base: for 0, 8 after a 0, or 16 after a 0x, which is passed over as it is for 16.
    private static int Radix(string s, ref int p, int radix)
    {
        if (radix == 0)
        {
            radix = 10;
            if (At(s, p) == '0')
            {
                radix = 8;
                if (At(s, p + 1) is 'x' or 'X')
                {
                    p += 2;
                    radix = 16;
                }
            }
        }
        else if (radix == 16 && At(s, p) == '0' && At(s, p + 1) is 'x' or 'X')
        {
            p += 2;
        }

        return radix;
    }

    // The digits of the base from p, and whether they overflow what the type holds.
    private static (T N, bool Ovfl) Digits<T>(string s, ref int p, int radix, T max)
        where T : IBinaryInteger<T>
    {
        T n = T.Zero, b = T.CreateTruncating(radix), m = max / b;
        bool ovfl = false;
        for (; Digit(At(s, p)) < radix; p++)
        {
            ovfl |= n > m;
            T nn = unchecked((n * b) + T.CreateTruncating(Digit(s[p])));
            ovfl |= nn < n;
            n = nn;
        }

        return (n, ovfl);
    }

    // The character at p, or NUL past the end, as a C string ends.
    private static char At(string s, int p) => p < s.Length ? s[p] : '\0';

    private static int Spaces(string s)
    {
        int p = 0;
        while (At(s, p) is ' ' or '\t' or '\n' or '\f' or '\r' or '\v')
        {
            p++;
        }

        return p;
    }

    private static int Digit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => c - 'a' + 10,
        >= 'A' and <= 'Z' => c - 'A' + 10,
        _ => 36,
    };

    // pow10: from the table, or the product of two halves, or their reciprocal.
    private static double Pow10(int n)
    {
        if (n < 0)
        {
            n = -n;
            return n < Tab.Length ? 1 / Tab[n] : 1 / (Pow10(n / 2) * Pow10(n - (n / 2)));
        }

        return n < Tab.Length ? Tab[n] : Pow10(n / 2) * Pow10(n - (n / 2));
    }

    // xdtoa: 17 digits of f taken by multiplying by ten, made exact against strtod, then rounded
    // to the precision and laid out in e or f style; g takes f style for an exponent from -4 to
    // the precision, and drops trailing zeros unless the flags have '#'.
    private static string Xdtoa(double f, char verb, int prec, string flags)
    {
        char[] s1 = new char[Nsignif];
        bool sign = BitConverter.DoubleToInt64Bits(f) < 0;
        bool ucase = false;
        char chr = verb;
        int e = 0;
        bool isnan = double.IsNaN(f);
        bool isinf = double.IsInfinity(f);
        if (!isnan && !isinf)
        {
            f = Math.Abs(f);
            if (char.IsAsciiLetterUpper(chr))
            {
                ucase = true;
                chr = char.ToLowerInvariant(chr);
            }

            double g = f;
            if (g != 0)
            {
                // frexp's exponent, as a power of ten.
                e = (int)((Math.ILogB(f) + 1) * .301029995664);
                (int d, double h) = e is >= -150 and <= 150 ? (0, f) : (e / 2, f * Pow10(-(e / 2)));
                g = h * Pow10(d - e);
                while (g < 1)
                {
                    e--;
                    g = h * Pow10(d - e);
                }

                while (g >= 10)
                {
                    e++;
                    g = h * Pow10(d - e);
                }
            }

            for (int i = 0; i < Nsignif; i++)
            {
                int d = (int)g;
                s1[i] = (char)(d + '0');
                g = (g - d) * 10;
            }

            // Decimal rounding, to be rid of a run of 9s, when the digits wanted run past 15.
            bool found = false;
            if (prec + 1 + (chr == 'f' ? e : 0) >= Nsignif - 2)
            {
                char[] s2 = (char[])s1.Clone();
                int d = e;
                s1[Nsignif - 2] = s1[Nsignif - 1] = '0';
                found = Strtod(s1, e) == f;
                if (!found && Xadd(s1, Nsignif - 3, 1))
                {
                    e++;
                }

                found = found || Strtod(s1, e) == f;
                if (!found)
                {
                    (s1, e) = (s2, d);
                }
            }

            // Converted back, so that s1 is exact.
            for (double g2; !found && (g2 = Strtod(s1, e)) != f;)
            {
                if (f > g2 ? Xadd(s1, Nsignif - 1, 1) : Xsub(s1, Nsignif - 1, 1))
                {
                    e += f > g2 ? -1 : 1;
                }
            }
        }

        var s = new StringBuilder();
        if (sign)
        {
            s.Append('-');
        }
        else if (flags.Contains('+', StringComparison.Ordinal))
        {
            s.Append('+');
        }
        else if (flags.Contains(' ', StringComparison.Ordinal))
        {
            s.Append(' ');
        }

        if (isnan || isinf)
        {
            return s.Append(isnan ? "NaN" : "Inf").ToString();
        }

        // c1 leading zeros, c2 digits of the conversion and c3 trailing zeros, c4 of all of them
        // after the point.
        if (chr == 'g' && prec > 0)
        {
            prec--;
        }

        int c1 = 0, c2 = prec + 1, c3 = 0, c4 = prec;
        switch (chr)
        {
            case 'g':
                e += Xadd(s1, c2, 5) ? 1 : 0;
                if (e >= -4 && e <= prec)
                {
                    (c1, c4, chr) = (-e, prec - e, 'h');
                }

                break;
            case 'f':
                e += Xadd(s1, c2 + e, 5) ? 1 : 0;
                c1 = -e > prec ? c2 : -e;
                c2 += e;
                break;
            default:
                e += Xadd(s1, c2, 5) ? 1 : 0;
                break;
        }

        c1 = Math.Max(c1, 0);
        c2 = Math.Max(c2, 0);
        if (c2 > Nsignif)
        {
            (c3, c2) = (c2 - Nsignif, Nsignif);
        }

        for (; c1 > 0; c1--)
        {
            s.Append(c1 + c2 + c3 == c4 ? ".0" : "0");
        }

        for (int i = 0; c2 > 0; c2--)
        {
            s.Append(c2 + c3 == c4 ? "." : string.Empty).Append(s1[i++]);
        }

        for (; c3 > 0; c3--)
        {
            s.Append(c3 == c4 ? ".0" : "0");
        }

        if (flags.Contains('#', StringComparison.Ordinal))
        {
            s.Append(c4 == 0 ? "." : string.Empty);
        }
        else if (chr is 'g' or 'h')
        {
            int n = s.Length - 1;
            while (n >= 0 && s[n] == '0')
            {
                n--;
            }

            int point = s.ToString().LastIndexOf('.', Math.Max(n, 0));
            if (n >= 0 && point >= 0)
            {
                s.Length = point == n ? n : n + 1;
            }
        }

        if (chr is 'e' or 'g')
        {
            s.Append(ucase ? 'E' : 'e').Append(e < 0 ? '-' : '+');
            int x = Math.Abs(e);
            if (x >= 100)
            {
                s.Append((char)((x / 100) + '0'));
                x %= 100;
            }

            s.Append((char)((x / 10) + '0')).Append((char)((x % 10) + '0'));
        }

        return s.ToString();
    }

    private static double Strtod(char[] digits, int e) => double.Parse($"{new string(digits)}e{e - Nsignif + 1}", CultureInfo.InvariantCulture);

    // xadd: v added at digit n, carried up; true when it carried out of the first, which is then 1.
    private static bool Xadd(char[] a, int n, int v)
    {
        if (n < 0 || n >= Nsignif)
        {
            return false;
        }

        for (int b = n; b >= 0; b--)
        {
            int c = a[b] + v;
            if (c <= '9')
            {
                a[b] = (char)c;
                return false;
            }

            a[b] = '0';
            v = 1;
        }

        a[0] = '1';
        return true;
    }

    // xsub: v taken from digit n, borrowed down; true when it borrowed past the first, then 9.
    private static bool Xsub(char[] a, int n, int v)
    {
        for (int b = n; b >= 0; b--)
        {
            int c = a[b] - v;
            if (c >= '0')
            {
                a[b] = (char)c;
                return false;
            }

            a[b] = '9';
            v = 1;
        }

        a[0] = '9';
        return true;
    }
}
