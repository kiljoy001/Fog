using System.Numerics;

namespace NinePSharp.Fog.Kernel;

// Plan 9 libc's strtol, strtoll and strtoul, whose longs are 32 bits: the number at the start of s,
// and in end where it stops, or 0 when no digit was read; and %q as quotefmtinstall gives it.
public static class Libc
{
    private const string RcSpecial = "`^#*[]=|\\?${}()'<>&;";

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
}
