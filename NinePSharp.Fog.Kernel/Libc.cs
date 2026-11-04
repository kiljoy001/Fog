using System.Globalization;
using System.Numerics;
using System.Text;
using NinePSharp.Constants;
using Plan9.Utf;

namespace NinePSharp.Fog.Kernel;

// Plan 9 libc's strtol, strtoll and strtoul, whose longs are 32 bits: the number at the start of s,
// and in end where it stops, or 0 when no digit was read; %q as quotefmtinstall gives it; sprint,
// with fmt's verbs and fltfmt.c's %e, %f and %g; and getwd.
public static class Libc
{
    private const string RcSpecial = "`^#*[]=|\\?${}()'<>&;";
    private const int FDigit = 30;
    private const int FDeflt = 6;
    private const int Nsignif = 17;

    // fmt.h's flags.
    private const int FmtWidth = 1;
    private const int FmtLeft = FmtWidth << 1;
    private const int FmtPrec = FmtLeft << 1;
    private const int FmtSharp = FmtPrec << 1;
    private const int FmtSpace = FmtSharp << 1;
    private const int FmtSign = FmtSpace << 1;
    private const int FmtZero = FmtSign << 1;
    private const int FmtUnsigned = FmtZero << 1;
    private const int FmtShort = FmtUnsigned << 1;
    private const int FmtLong = FmtShort << 1;
    private const int FmtVLong = FmtLong << 1;
    private const int FmtComma = FmtVLong << 1;
    private const int FmtByte = FmtComma << 1;

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

    // sprint, as dofmt.c and fmt.c run it: the format's text as it is, and each conversion with
    // 9front's verbs and flags, its arguments read from args as 6c passes them, each an eight-byte
    // slot that the verb reads as an int, long, vlong, double or pointer; %r prints errstr. What
    // is formatted before a conversion the format ends in is kept, as vsnprint keeps it.
    public static byte[] Sprint(string format, IReadOnlyList<FmtArg> args, string errstr = "")
        => Sprint(Encoding.UTF8.GetBytes(format), args, errstr);

    // sprint of a format of bytes; with quotefmt, %q and %Q quote as quotefmtinstall has them.
    public static byte[] Sprint(byte[] format, IReadOnlyList<FmtArg> args, string errstr = "", bool quotefmt = false)
    {
        var f = new Fmt(args, errstr) { Quotefmt = quotefmt };
        byte[] fmt = [.. format, 0];
        for (int p = 0; ;)
        {
            while (fmt[p] is not (0 or (byte)'%'))
            {
                f.Out.Add(fmt[p++]);
            }

            // Stryker disable once Equality : Dispatch gives -1 or a place past the %, never 0
            if (fmt[p++] == 0 || (p = Dispatch(f, fmt, p)) < 0)
            {
                return [.. f.Out];
            }
        }
    }

    // ctime: asctime's "WW MMM _D hh:mm:ss ZZZ YYYY\n" for the time, in GMT, Fog's only zone.
    public static string Ctime(long seconds)
    {
        DateTime t = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{t:ddd MMM} {t.Day,2} {t:HH:mm:ss} GMT {t.Year}\n");
    }

    public static int Strtol(string s, int radix, out int end) => Signed<int>(s, radix, out end);

    public static long Strtoll(string s, int radix, out int end) => Signed<long>(s, radix, out end);

    // strtod: the number at the start of s, read by strtod.c's states: blanks, a sign, digits with a
    // point, and an exponent, which any '-' past the first sign makes negative; or nan, inf or
    // infinity in any case. end is where it stops, or 0 when no digit was read; the digits past
    // 1450 are dropped, and the value is the closest double, as strtod.c finds it.
    public static double Strtod(string s, out int end)
    {
        const int Ndig = 1500;
        var a = new StringBuilder();
        bool sign = false, esign = false, dpoint = false;
        int dp = 0, ex = 0, state = 0, i = 0;
        for (; ; i++)
        {
            char c = At(s, i);
            if (c is >= '0' and <= '9')
            {
                if (state >= 5)
                {
                    state = 7;
                    ex = unchecked((ex * 10) + c - '0');
                    continue;
                }

                state = state <= 2 ? 2 : 4;
                if (a.Length == 0 && c == '0')
                {
                    dp--;
                }
                else if (a.Length < Ndig - 50)
                {
                    a.Append(c);
                }

                continue;
            }

            if (c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' && state == 0)
            {
                continue;
            }

            if (c is '-' or '+')
            {
                if (c == '-')
                {
                    // A sign past the first, which then ends the number, negates the exponent.
                    (sign, esign) = state == 0 ? (true, esign) : (sign, true);
                }

                if (state is 0 or 5)
                {
                    state = state == 0 ? 1 : 6;
                    continue;
                }
            }
            else if (c == '.')
            {
                (dpoint, dp) = (true, a.Length);
                if (state is 0 or 1 or 2)
                {
                    state = state == 2 ? 4 : 3;
                    continue;
                }
            }
            else if (c is 'e' or 'E' && state is 2 or 4)
            {
                state = 5;
                continue;
            }

            break;
        }

        string rest = s[Math.Min(i, s.Length)..].ToLowerInvariant();
        if (state is 0 or 1 && rest.StartsWith("nan", StringComparison.Ordinal))
        {
            // NaN(), whose bits are 9front's.
            end = i + 3;
            return BitConverter.Int64BitsToDouble(sign ? unchecked((long)0xfff0000000000001) : 0x7ff0000000000001);
        }

        if (state is 0 or 1 && rest.StartsWith("inf", StringComparison.Ordinal))
        {
            end = i + (rest.StartsWith("infinity", StringComparison.Ordinal) ? 8 : 3);
            return sign ? double.NegativeInfinity : double.PositiveInfinity;
        }

        end = state is 0 or 1 or 3 ? 0 : i - (state == 6 ? 2 : state == 5 ? 1 : 0);

        // strtod.c drops trailing zeros after a point, and gives 0 or Inf for an exponent past
        // -327 or 310; the closest double is the same without either.
        string digits = a.ToString();
        dp = unchecked((dpoint ? dp : digits.Length) + (esign ? -ex : ex));
        double d = double.Parse($"0.{digits}e{dp}", CultureInfo.InvariantCulture);
        return sign ? -d : d;
    }

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

    // pow10: from the table, or the product of two halves, or their reciprocal.
    internal static double Pow10(int n)
    {
        // Stryker disable once Equality : pow10(0) is 1 by either branch
        if (n < 0)
        {
            n = -n;
            return n < Tab.Length ? 1 / Tab[n] : 1 / (Pow10(n / 2) * Pow10(n - (n / 2)));
        }

        return n < Tab.Length ? Tab[n] : Pow10(n / 2) * Pow10(n - (n / 2));
    }

    // xadd: v added at digit n, carried up; true when it carried out of the first, which is then 1.
    internal static bool Xadd(char[] a, int n, int v)
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
    internal static bool Xsub(char[] a, int n, int v)
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

    // _fmtdispatch: the flags, width and precision of a conversion and then its verb; where the
    // format goes on after it, or -1 when the format ends first.
    private static int Dispatch(Fmt f, byte[] fmt, int p)
    {
        (f.Flags, f.Width, f.Prec) = (0, 0, 0);
        for (; ;)
        {
            p += Utf.CharToRune(out uint r, fmt.AsSpan(p));
            f.R = r;
            int i;
            switch (r)
            {
                case 0:
                    return -1;
                case '.':
                    f.Flags |= FmtWidth | FmtPrec;
                    continue;
                case '0' when (f.Flags & FmtWidth) == 0:
                    f.Flags |= FmtZero;
                    continue;
                case >= '0' and <= '9':
                    // The digits are read as signed chars.
                    for (i = 0; r is >= '0' and <= '9'; r = unchecked((uint)(sbyte)fmt[p++]))
                    {
                        i = unchecked((i * 10) + (int)r - '0');
                    }

                    p--;
                    break;
                case '*':
                    i = (int)f.Next().Bits;
                    if (i < 0)
                    {
                        if ((f.Flags & FmtPrec) != 0)
                        {
                            (f.Flags, f.Prec) = (f.Flags & ~FmtPrec, 0);
                            continue;
                        }

                        (i, f.Flags) = (unchecked(-i), f.Flags | FmtLeft);
                    }

                    break;
                default:
                    if (Verb(f, r) == 0)
                    {
                        return p;
                    }

                    continue;
            }

            // numflag: a number is the width, or the precision after a point.
            if ((f.Flags & FmtWidth) != 0)
            {
                (f.Flags, f.Prec) = (f.Flags | FmtPrec, i);
            }
            else
            {
                (f.Flags, f.Width) = (f.Flags | FmtWidth, i);
            }
        }
    }

    // fmtfmt's verb: 1 for a flag, which the conversion goes on after, 0 once it is done.
    private static int Verb(Fmt f, uint r)
    {
        switch (r)
        {
            case ' ' or '#' or '+' or ',' or '-' or 'h' or 'l' or 'u' or 'z':
                Flagfmt(f, r);
                return 1;
            case '%':
                f.Prec = 1;
                Fmtrcpy(f, [r], 1);
                break;
            case 'C':
                Fmtrcpy(f, [(uint)(int)f.Next().Bits], 1);
                break;
            case 'E' or 'G' or 'e' or 'f' or 'g':
                string flags = ((f.Flags & FmtSign) != 0 ? "+" : string.Empty) + ((f.Flags & FmtSpace) != 0 ? " " : string.Empty) + ((f.Flags & FmtSharp) != 0 ? "#" : string.Empty);
                byte[] s = Encoding.ASCII.GetBytes(Xdtoa(BitConverter.Int64BitsToDouble(f.Next().Bits), (char)r, Math.Min((f.Flags & FmtPrec) != 0 ? f.Prec : FDeflt, FDigit), flags));
                f.Flags &= FmtWidth | FmtLeft;
                Fmtcpy(f, s, s.Length, s.Length);
                break;
            case 'S':
                if (f.Next().String() is not { } t)
                {
                    Fmtcpy(f, "<nil>"u8.ToArray(), 5, 5);
                    break;
                }

                // A Rune string, as far as a NUL, or its precision.
                uint[] runes = [.. t.EnumerateRunes().Select(x => (uint)x.Value).TakeWhile(x => x != 0)];
                Fmtrcpy(f, runes, runes.Length);
                break;
            case 'X' or 'b' or 'd' or 'o' or 'p' or 'x':
                Ifmt(f);
                break;
            case 'c':
                f.Prec = 1;
                Fmtcpy(f, [(byte)f.Next().Bits], 1, 1);
                break;
            case 'n':
                throw new ArgumentException("%n writes where the slot points, which is not a pointer");
            case 'r':
                Fmtstrcpy(f, Encoding.UTF8.GetBytes(f.Errstr));
                break;
            case 's':
                Fmtstrcpy(f, f.Next().Bytes());
                break;
            case 'q' when f.Quotefmt:
                Quotestrfmt(f, f.Next().Bytes());
                break;
            case 'Q' when f.Quotefmt:
                Quotestrfmt(f, f.Next().String() is { } q ? Encoding.UTF8.GetBytes(q) : null);
                break;
            default:
                // _badfmt: the verb between percent signs.
                byte[] x = [(byte)'%', .. Rune(r), (byte)'%'];
                f.Prec = x.Length;
                Fmtcpy(f, x, x.Length, x.Length);
                break;
        }

        return 0;
    }

    private static void Flagfmt(Fmt f, uint r)
    {
        f.Flags |= r switch
        {
            ',' => FmtComma,
            '-' => FmtLeft,
            '+' => FmtSign,
            '#' => FmtSharp,
            ' ' => FmtSpace,
            'u' => FmtUnsigned,
            'h' => (f.Flags & FmtShort) != 0 ? FmtByte | FmtShort : FmtShort,
            'l' => (f.Flags & FmtLong) != 0 ? FmtVLong | FmtLong : FmtLong,
            _ => FmtLong | FmtVLong,
        };
    }

    // _ifmt: an integer of the size the flags say, in the verb's base, signed unless 'u', with
    // its precision in zeros, then its sign or prefix, and zeros to the width with '0'.
    private static void Ifmt(Fmt f)
    {
        int fl = f.Flags;
        bool isv = false, neg = false;
        ulong vu = 0;
        uint u = 0;
        long bits = f.Next().Bits;
        if (f.R == 'p')
        {
            (vu, isv, f.R) = ((ulong)bits, true, 'x');
            fl |= FmtUnsigned;
        }
        else if ((fl & FmtVLong) != 0)
        {
            (vu, isv) = ((ulong)bits, true);
        }
        else
        {
            int v = (int)bits;
            bool unsigned = (fl & FmtUnsigned) != 0;
            u = (fl & FmtLong) != 0 ? (uint)v
                : (fl & FmtByte) != 0 ? (unsigned ? (byte)v : (uint)(sbyte)v)
                : (fl & FmtShort) != 0 ? (unsigned ? (ushort)v : (uint)(short)v)
                : (uint)v;
        }

        (int b, string conv) = f.R switch
        {
            'd' => (10, "0123456789abcdef"),
            'x' => (16, "0123456789abcdef"),
            'X' => (16, "0123456789ABCDEF"),
            'b' => (2, "0123456789abcdef"),
            _ => (8, "0123456789abcdef"),
        };
        if ((fl & FmtUnsigned) == 0)
        {
            // Stryker disable once Equality : the negative of 0 is 0
            if (isv && (long)vu < 0)
            {
                (vu, neg) = (unchecked((ulong)-(long)vu), true);
            }
            else if (!isv && (int)u < 0)
            {
                (u, neg) = (unchecked((uint)-(int)u), true);
            }
        }

        char[] buf = new char[70];
        int p = buf.Length - 1, n = 0;
        for (ulong rest = isv ? vu : u; rest != 0; rest /= (ulong)b)
        {
            if ((fl & FmtComma) != 0 && n % 4 == 3)
            {
                buf[p--] = ',';
                n++;
            }

            buf[p--] = conv[(int)(rest % (ulong)b)];
            n++;
        }

        if (n == 0)
        {
            buf[p--] = '0';
            n = 1;
        }

        for (; n < f.Prec && p > 3; n++)
        {
            buf[p--] = '0';
        }

        n += neg || (fl & (FmtSign | FmtSpace)) != 0 ? 1 : 0;
        if ((fl & FmtSharp) != 0 && b == 16)
        {
            n += 2;
        }
        else if ((fl & FmtSharp) != 0 && b == 8)
        {
            // A 0 takes no prefix 0, though one is put before it uncounted, as _ifmt puts it.
            // Stryker disable once all : the prefix 0 put before a 0 is past the n bytes copied
            if (buf[p + 1] == '0')
            {
                fl &= ~FmtSharp;
            }
            else
            {
                n++;
            }
        }

        if ((fl & FmtZero) != 0 && (fl & (FmtLeft | FmtPrec)) == 0)
        {
            for (; n < f.Width && p > 3; n++)
            {
                buf[p--] = '0';
            }

            f.Width = 0;
        }

        if ((fl & FmtSharp) != 0 && b is 16 or 8)
        {
            if (b == 16)
            {
                buf[p--] = (char)f.R;
            }

            buf[p--] = '0';
        }

        if (neg || (fl & (FmtSign | FmtSpace)) != 0)
        {
            buf[p--] = neg ? '-' : (fl & FmtSign) != 0 ? '+' : ' ';
        }

        f.Flags &= ~FmtPrec;
        byte[] digits = Encoding.ASCII.GetBytes(buf, p + 1, n);
        Fmtcpy(f, digits, n, n);
    }

    // fmtstrcpy: a string as far as its precision in runes, or <nil> for none.
    private static void Fmtstrcpy(Fmt f, byte[]? m)
    {
        if (m is null)
        {
            Fmtcpy(f, "<nil>"u8.ToArray(), 5, 5);
            return;
        }

        int nul = Array.IndexOf(m, (byte)0);
        m = nul < 0 ? m : m[..nul];
        if ((f.Flags & FmtPrec) == 0)
        {
            Fmtcpy(f, m, Utf.UtfLen(m), m.Length);
            return;
        }

        int i = 0, j = 0;

        // Stryker disable once Equality : a rune past the precision is not copied, as _fmtcpy stops there
        for (; j < f.Prec && i < m.Length; j++)
        {
            i += Utf.CharToRune(out _, m.AsSpan(i));
        }

        Fmtcpy(f, m, j, i);
    }

    // _quotestrfmt of a string of bytes: as it is, unless it is empty, # is given, or it holds a
    // blank, a control character or a quote, when it is put in quotes, each quote in it doubled; as
    // far as its precision in runes, the quotes counted in its width.
    private static void Quotestrfmt(Fmt f, byte[]? s)
    {
        if (s is null)
        {
            Fmtcpy(f, "<nil>"u8.ToArray(), 5, 5);
            return;
        }

        // _quotesetup, with no limit to the output.
        int nul = Array.IndexOf(s, (byte)0);
        s = nul < 0 ? s : s[..nul];
        int nin = (f.Flags & FmtPrec) != 0 ? f.Prec : -1;
        bool quoted = (f.Flags & FmtSharp) != 0 || nin == 0 || s.Length == 0;
        int nbytesout = quoted ? 2 : 0, nbytesin = 0, nrunesin = 0;
        for (; nin != 0 && nbytesin < s.Length; nin--)
        {
            int w = Utf.CharToRune(out uint c, s.AsSpan(nbytesin));
            if (c <= ' ' || c == '\'')
            {
                nbytesout += quoted ? 0 : 2;
                quoted = true;
                nbytesout += c == '\'' ? 1 : 0;
            }

            nbytesin += w;
            nrunesin++;
            nbytesout += w;
        }

        if (!quoted)
        {
            Fmtcpy(f, s, nrunesin, nbytesin);
            return;
        }

        // qstrfmt.
        Pad(f, (f.Flags & FmtLeft) == 0 ? f.Width - nbytesout : 0);
        f.Out.Add((byte)'\'');
        for (int m = 0, nc = nrunesin; nc > 0; nc--)
        {
            uint r = s[m];
            if (r < Utf.RuneSelf)
            {
                m++;
            }

            // Stryker disable once Equality : UtfMax bytes are always a full rune
            else if (nbytesin - m >= Utf.UtfMax || Utf.FullRune(s.AsSpan(m, nbytesin - m)))
            {
                m += Utf.CharToRune(out r, s.AsSpan(m, nbytesin - m));
            }
            else
            {
                break;
            }

            f.Out.AddRange(Rune(r));
            f.Out.AddRange(r == '\'' ? Rune(r) : []);
        }

        f.Out.Add((byte)'\'');
        Pad(f, (f.Flags & FmtLeft) != 0 ? f.Width - nbytesout : 0);
    }

    // _fmtcpy: n runes of the sz bytes at m, no more than the precision, padded to the width with
    // spaces, on the left unless '-'; a rune cut short ends it. Past the bytes, which _badfmt's
    // count of a verb's bytes as runes reads, it finds NULs, as 9front finds them there.
    private static void Fmtcpy(Fmt f, byte[] m, int n, int sz)
    {
        n = (f.Flags & FmtPrec) != 0 && n > f.Prec ? f.Prec : n;
        Pad(f, (f.Flags & FmtLeft) == 0 ? f.Width - n : 0);
        for (int i = 0, nc = n; nc > 0; nc--)
        {
            if (i >= sz || m[i] < Utf.RuneSelf)
            {
                f.Out.Add(i < sz ? m[i] : (byte)0);
                i++;
            }
            else if (sz - i >= Utf.UtfMax || Utf.FullRune(m.AsSpan(i, sz - i)))
            {
                i += Utf.CharToRune(out uint r, m.AsSpan(i, sz - i));
                f.Out.AddRange(Rune(r));
            }
            else
            {
                break;
            }
        }

        Pad(f, (f.Flags & FmtLeft) != 0 ? f.Width - n : 0);
    }

    // _fmtrcpy: n runes, no more than the precision, padded as _fmtcpy pads them.
    private static void Fmtrcpy(Fmt f, uint[] m, int n)
    {
        n = (f.Flags & FmtPrec) != 0 ? Math.Min(n, f.Prec) : n;
        Pad(f, (f.Flags & FmtLeft) == 0 ? f.Width - n : 0);
        foreach (uint r in m.Take(n))
        {
            f.Out.AddRange(Rune(r));
        }

        Pad(f, (f.Flags & FmtLeft) != 0 ? f.Width - n : 0);
    }

    private static void Pad(Fmt f, int n)
    {
        for (int i = 0; i < n; i++)
        {
            f.Out.Add((byte)' ');
        }
    }

    private static byte[] Rune(uint r)
    {
        byte[] b = new byte[Utf.UtfMax];
        return b[..Utf.RuneToChar(b, r)];
    }

    private static double Strtod(char[] digits, int e) => double.Parse($"{new string(digits)}e{e - Nsignif + 1}", CultureInfo.InvariantCulture);

    // A varargs slot: eight bytes, which a verb reads as an int, long, vlong, double or pointer, or
    // a string, for %s and %S. A slot past the last reads as zeros, where 9front reads its stack.
    public readonly record struct FmtArg(long Bits, string? Text)
    {
        public byte[]? Raw { get; init; }

        public static FmtArg Of(double d) => new(BitConverter.DoubleToInt64Bits(d), null);

        public static FmtArg Of(long v) => new(v, null);

        public static FmtArg Of(string s) => new(0, s);

        // A string of bytes as they are, which need not be UTF-8.
        public static FmtArg Of(byte[] s) => new(0, null) { Raw = s };

        // The slot as a pointer to a string: nil for zeros; anything else is no string, which 9front
        // faults on reading.
        public string? String() => Text ?? (Bits == 0 ? null : throw new ArgumentException("a number where the format reads a string"));

        // The string's bytes: those given, or the UTF-8 of the text.
        public byte[]? Bytes() => Raw ?? (String() is { } t ? Encoding.UTF8.GetBytes(t) : null);
    }

    // fmt.h's Fmt, as far as sprint uses it.
    private sealed class Fmt(IReadOnlyList<FmtArg> args, string errstr)
    {
        private int next;

        public List<byte> Out { get; } = [];

        public string Errstr => errstr;

        public bool Quotefmt { get; init; }

        public uint R { get; set; }

        public int Flags { get; set; }

        public int Width { get; set; }

        public int Prec { get; set; }

        // va_arg: the next slot.
        public FmtArg Next() => next < args.Count ? args[next++] : default;
    }
}
