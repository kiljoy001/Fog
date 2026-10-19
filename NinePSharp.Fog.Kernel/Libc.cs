using System.Numerics;

namespace NinePSharp.Fog.Kernel;

// Plan 9 libc's strtol, strtoll and strtoul, whose longs are 32 bits: the number at the start of s,
// and in end where it stops, or 0 when no digit was read.
public static class Libc
{
    public static int Strtol(string s, int radix, out int end) => Signed<int>(s, radix, out end);

    public static long Strtoll(string s, int radix, out int end) => Signed<long>(s, radix, out end);

    // strtoul skips a 0x only before a hex digit, and gives the largest for an overflow.
    public static uint Strtoul(string s, int radix, out int end)
    {
        int p = Spaces(s);
        bool neg = At(s, p) == '-';
        if (At(s, p) is '-' or '+')
        {
            p++;
        }

        if (radix == 0)
        {
            radix = At(s, p) != '0' ? 10 : At(s, p + 1) is 'x' or 'X' ? 16 : 8;
        }

        uint n = 0;
        int ndig = 0;
        bool ovfl = false;
        if (radix is >= 2 and <= 36)
        {
            if (radix == 16 && At(s, p) == '0' && At(s, p + 1) is 'x' or 'X' && Digit(At(s, p + 2)) < 16)
            {
                p += 2;
            }

            uint m = uint.MaxValue / (uint)radix;
            for (; Digit(At(s, p)) < radix; p++, ndig++)
            {
                ovfl |= n > m;
                uint nn = unchecked((n * (uint)radix) + (uint)Digit(s[p]));
                ovfl |= nn < n;
                n = nn;
            }
        }

        end = ndig == 0 ? 0 : p;
        return ovfl ? uint.MaxValue : neg ? unchecked(0u - n) : n;
    }

    private static T Signed<T>(string s, int radix, out int end)
        where T : IBinaryInteger<T>, IMinMaxValue<T>
    {
        int p = Spaces(s);
        bool neg = At(s, p) == '-';
        if (At(s, p) is '-' or '+')
        {
            p++;
        }

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

        T n = T.Zero;
        int ndig = 0;
        bool ovfl = false;
        if (radix is >= 1 and <= 36)
        {
            T b = T.CreateTruncating(radix);
            T m = T.MaxValue / b;
            for (; Digit(At(s, p)) < radix; p++, ndig++)
            {
                ovfl |= n > m;
                T nn = (n * b) + T.CreateTruncating(Digit(s[p]));
                ovfl |= nn < n;
                n = nn;
            }
        }

        end = ndig == 0 ? 0 : p;
        return ovfl ? (neg ? T.MinValue : T.MaxValue) : neg ? -n : n;
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
