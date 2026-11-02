using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// libc's date.c, as far as date(1) uses it: a zone loaded from its file, a time split into its
// fields in a zone and put back together, and τconv, which lays the fields out by a format of
// letters, each run of one letter a field as wide as the run.
public sealed class Tm
{
    public const string Ctimefmt = "WW MMM _D hh:mm:ss ZZZ YYYY";
    private const int Tzsize = 150;
    private const long Nsec = 1000 * 1000 * 1000;
    private const long Daysec = 24L * 3600;
    private const long Days400y = (365 * 400) + (4 * 25) - 3;
    private const long Days4y = (365 * 4) + 1;

    private static readonly int[] Mdays = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    private static readonly string[] Wdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private int sec;
    private int min;
    private int hour;
    private int mday;
    private int mon;
    private int year;
    private int wday;
    private int nsec;
    private int tzoff;

    // Stryker disable once String : tmtimens names the zone before anything reads it
    private string zone = string.Empty;
    private Tzone? tz;

    // The fields of C's Tm, which a program may set before tmnorm finds the time they give.
    public int Sec { get => sec; set => sec = value; }

    public int Min { get => min; set => min = value; }

    public int Hour { get => hour; set => hour = value; }

    public int Mday { get => mday; set => mday = value; }

    public int Mon { get => mon; set => mon = value; }

    public int Year { get => year; set => year = value; }

    public int Nanosec { get => nsec; set => nsec = value; }

    // tzload: the zone of the name, "local" being $timezone's, or null with errstr set.
    public static async Task<Tzone?> TzloadAsync(Process process, string tzname)
    {
        if (tzname.Length >= 32)
        {
            Errstr.Set(process, "timezone name too long");
            return null;
        }

        string path = tzname == "local" ? "/env/timezone" : $"/adm/timezone/{tzname}";
        ReadOnlyMemory<byte> read;
        try
        {
            // Stryker disable once Bitwise : the file is closed before tzload returns, so it is never open across an exec
            int f = await process.OpenAsync(path, NinePConstants.OREAD | NinePConstants.OCEXEC);
            try
            {
                read = await process.ReadAsync(f, (Tzsize * 11) + 30);
            }
            finally
            {
                // Stryker disable once Statement : a descriptor left open changes no zone tzload gives
                await process.CloseAsync(f);
            }
        }
        catch (SyscallException e)
        {
            Errstr.Set(process, e.Message);
            return null;
        }

        // A file that fills the buffer is too long to be a zone's, and errstr is left as it was.
        if (read.Length >= (Tzsize * 11) + 30)
        {
            return null;
        }

        byte[] buf = [.. read.Span, 0];
        int p = 0;

        // Stryker disable once String : a name is read into each before the zone is made of them, or an error is returned
        string stname = string.Empty, dlname = string.Empty;
        int stdiff = 0, dldiff = 0;
        string? error = Rdname(buf, ref p, out stname) ?? Rdlong(buf, ref p, out stdiff)
            ?? Rdname(buf, ref p, out dlname) ?? Rdlong(buf, ref p, out dldiff);
        var zone = new Tzone(stname, stdiff, dlname, dldiff);
        for (int i = 0; error is null && i < Tzsize; i++)
        {
            if (Rdlong(buf, ref p, out int pair) is not null)
            {
                error = "invalid transition time";
                break;
            }

            if (pair == 0)
            {
                return zone;
            }

            zone.Dlpairs.Add(pair);
        }

        Errstr.Set(process, error ?? $"invalid timezone {tzname}");
        return null;
    }

    // tmtimens: the time, seconds and nanoseconds after the epoch, in the zone, or GMT for none.
    public static Tm Tmtimens(long abs, long ns, Tzone? tz)
    {
        var tm = new Tm { tz = tz };
        tm.Tzoffset(tz, abs);
        tm.Tmfill(abs, ns);
        return tm;
    }

    // tmnorm: the time the fields give, its zone's offset found again for it.
    public long Tmnorm()
    {
        (long m, long y) = mon > 1 ? (mon - 2, year + 1900L) : (mon + 10, year + 1899L);
        long c = y / 100;
        long yadj = y - (100 * c);
        long j = (c * Days400y / 4) + (Days4y * yadj / 4) + (((153 * m) + 2) / 5) + mday - 719469;
        long abs = unchecked((j * Daysec) + (hour * 3600) + (min * 60) + sec);
        if (tz is not null)
        {
            Tzoffset(tz, abs - tzoff);
            Tzoffset(tz, abs - tzoff);
        }

        abs -= tzoff;
        Tmfill(abs, nsec);
        return abs;
    }

    // τconv: the fields as the format lays them out, or null, with the error, for a format it
    // cannot take.
    public byte[]? Format(string fmtstr, out string? error)
    {
        error = null;
        byte[] fmt = [.. Encoding.UTF8.GetBytes(fmtstr), 0];
        var f = new List<byte>();
        for (int p = 0; fmt[p] != 0;)
        {
            int w = 1, pad = 0;
            while (fmt[p] == '_')
            {
                pad++;
                p++;
            }

            byte c0 = fmt[p];
            if (c0 == 0)
            {
                break;
            }

            for (p++; fmt[p] == c0; p++)
            {
                w++;
            }

            pad += w;
            bool ok = c0 switch
            {
                (byte)'?' => true,
                (byte)'Y' => w switch
                {
                    1 or 4 => Print(f, "%*d", pad, year + 1900),
                    2 => Print(f, "%*d", pad, year % 100),
                    _ => false,
                },
                (byte)'M' => w switch
                {
                    1 => Print(f, "%*d", pad, mon + 1),
                    2 => Print(f, "%*s%02d", pad - 2, string.Empty, mon + 1),
                    3 => Print(f, "%*.3s", pad, Name(Months, mon)),
                    4 => Print(f, "%*s", pad, Name(Months, mon)),
                    _ => false,
                },
                (byte)'D' => Twodigits(f, w, pad, mday),
                (byte)'o' => w == 1 && Print(f, mday switch { 1 or 21 or 31 => "st", 2 or 22 => "nd", 3 or 23 => "rd", _ => "th" }),
                (byte)'W' => w switch
                {
                    1 => Print(f, "%*d", pad, wday + 1),
                    2 => Print(f, "%*.3s", pad, Wdays[wday]),
                    3 => Print(f, "%*s", pad, Wdays[wday]),
                    _ => false,
                },
                (byte)'H' => Twodigits(f, w, pad, hour % 12),
                (byte)'h' => Twodigits(f, w, pad, hour),
                (byte)'m' => Twodigits(f, w, pad, min),
                (byte)'s' => Twodigits(f, w, pad, sec),
                (byte)'t' => w switch
                {
                    1 => Print(f, "%*d", pad, (nsec / (1000 * 1000)) % 1000),
                    2 or 3 => Print(f, "%*s%03d", P(pad, 3), string.Empty, (nsec / (1000 * 1000)) % 1000),
                    _ => false,
                },
                (byte)'u' => w switch
                {
                    1 => Print(f, "%*d", pad, (nsec / 1000) % 1000),
                    2 => Print(f, "%*s%03d", P(pad, 3), string.Empty, (nsec / 1000) % 1000),
                    3 => Print(f, "%*d", P(pad, 6), nsec / 1000),
                    4 => Print(f, "%*s%06d", P(pad, 6), string.Empty, nsec / 1000),
                    _ => false,
                },
                (byte)'n' => w switch
                {
                    1 => Print(f, "%*d", pad, nsec % 1000),
                    2 => Print(f, "%*s%03d", P(pad, 3), string.Empty, nsec % 1000),
                    3 => Print(f, "%*d", pad, nsec % (1000 * 1000)),
                    4 => Print(f, "%*s%06d", P(pad, 6), string.Empty, nsec % 1000000),
                    5 => Print(f, "%*d", pad, nsec),
                    6 => Print(f, "%*s%09d", P(pad, 9), string.Empty, nsec),
                    _ => false,
                },
                (byte)'z' => w == 1 && Offset(f, w, pad),
                (byte)'Z' => Offset(f, w, pad),
                (byte)'A' => w == 1 && Print(f, "%*s", pad, hour < 12 ? "AM" : "PM"),
                (byte)'a' => w == 1 && Print(f, "%*s", pad, hour < 12 ? "am" : "pm"),
                (byte)'[' => Quoted(f, fmt, ref p),
                _ => Repeat(f, c0, w),
            };
            if (!ok)
            {
                error = $"garbled format {fmtstr}";
                return null;
            }
        }

        return [.. f];
    }

    // rdname: a name in the zone's file, past the blanks before it, of three bytes to sixteen,
    // and a blank after it.
    private static string? Rdname(byte[] buf, ref int f, out string name)
    {
        const int n = 16;
        int s = f;
        while (buf[s] is (byte)' ' or (byte)'\t' or (byte)'\n')
        {
            s++;
        }

        int start = s, e = s + n;
        while (buf[s] is not (0 or (byte)' ' or (byte)'\t' or (byte)'\n') && s != e)
        {
            s++;
        }

        name = Encoding.UTF8.GetString(buf, start, s - start);
        if (s - start < 3 || buf[s] is not ((byte)' ' or (byte)'\t' or (byte)'\n'))
        {
            return "truncated name";
        }

        f = s;
        return null;
    }

    // rdlong: a number in the zone's file, negative for any '-' among the blanks before it, and 0
    // at the file's end; a long, so of 32 bits. A number the file ends in, with no blank after
    // it, meets the NUL there, which as %c ends the message.
    private static string? Rdlong(byte[] buf, ref int f, out int value)
    {
        int c;
        bool neg = false;
        value = 0;
        while ((c = buf[f]) != 0)
        {
            f++;
            if (c == '-')
            {
                neg = true;
            }
            else if (c is not (' ' or '\n'))
            {
                break;
            }
        }

        if (c == 0)
        {
            return null;
        }

        // Stryker disable once Conditional : past the NUL the next byte is 0 all the same, and the error returns before f is read again
        for (; c is not (' ' or '\n'); c = buf[f] == 0 ? 0 : buf[f++])
        {
            if (c is < '0' or > '9')
            {
                return c == 0 ? "non-number " : $"non-number {(char)c} in name";
            }

            value = unchecked((value * 10) + c - '0');
        }

        value = neg ? unchecked(-value) : value;
        return null;
    }

    private static long Mod(long a, long b)
    {
        long r = a % b;
        return r < 0 ? r + b : r;
    }

    // Stryker disable once Equality : at pad equal to w, pad - w is 0 too
    private static long P(long pad, long w) => pad < w ? 0 : pad - w;

    private static bool Print(List<byte> f, string format, params object?[] args)
    {
        f.AddRange(Libc.Sprint(format, [.. args.Select(a => a is null or string ? Libc.FmtArg.Of((string?)a!) : Libc.FmtArg.Of(Convert.ToInt64(a, System.Globalization.CultureInfo.InvariantCulture)))]));
        return true;
    }

    // A month's name, nil before January as months[-1] is in 9front's date.c, which %s prints as
    // <nil>.
    private static string? Name(string[] names, int i) => i >= 0 ? names[i] : null;

    private static bool Twodigits(List<byte> f, int w, long pad, int v) => w switch
    {
        1 => Print(f, "%*d", pad, v),
        2 => Print(f, "%*s%02d", pad - 2, string.Empty, v),
        _ => false,
    };

    // A [quoted] run, printed as it is but for a backslash before a byte; a bracket in it opens
    // another that the next one closes.
    private static bool Quoted(List<byte> f, byte[] fmt, ref int p)
    {
        for (int depth = 1; fmt[p] != 0;)
        {
            depth += fmt[p] == '[' ? 1 : fmt[p] == ']' ? -1 : 0;
            p += fmt[p] == '\\' && fmt[p + 1] != 0 ? 1 : 0;
            if (depth == 0)
            {
                break;
            }

            f.Add(fmt[p++]);
        }

        return fmt[p++] == ']';
    }

    // fmtrune of a char: a byte past ASCII is a negative char, below Runeself, and so the byte.
    private static bool Repeat(List<byte> f, byte c0, int w)
    {
        f.AddRange(Enumerable.Repeat(c0, w));
        return true;
    }

    // The name as snprint leaves it in Tm's zone of 16 bytes: the runes of it that fit in 15.
    private static string Zonename(string name)
    {
        var fits = new StringBuilder();
        int n = 0;
        foreach (Rune r in name.EnumerateRunes())
        {
            if ((n += r.Utf8SequenceLength) > 15)
            {
                break;
            }

            fits.Append(r.ToString());
        }

        return fits.ToString();
    }

    // Z and ZZ, the offset as +hhmm and +hh:mm, at least five wide; ZZZ the zone's name, and a
    // longer run nothing.
    private bool Offset(List<byte> f, int w, int pad)
    {
        char sgn = tzoff < 0 ? '-' : '+';
        int off = Math.Abs(tzoff);

        // Stryker disable once Equality : at pad 5 it is 5 either way
        pad = w < 3 && pad < 5 ? 5 : pad;
        return w switch
        {
            1 => Print(f, "%*s%c%02d%02d", pad - 5, string.Empty, sgn, off / 3600, (off / 60) % 60),
            2 => Print(f, "%*s%c%02d:%02d", pad - 5, string.Empty, sgn, off / 3600, (off / 60) % 60),
            3 => Print(f, "%*s", pad, zone),
            _ => true,
        };
    }

    // tzoffset: the zone's name and offset at the time, its daylight one within a pair of its
    // transitions.
    private void Tzoffset(Tzone? at, long abs)
    {
        if (at is null)
        {
            (zone, tzoff) = ("GMT", 0);
            return;
        }

        abs += at.Stdiff;
        bool dl = false;
        for (int i = 0; i < at.Dlpairs.Count; i += 2)
        {
            if (abs > at.Dlpairs[i] && abs <= (i + 1 < at.Dlpairs.Count ? at.Dlpairs[i + 1] : 0))
            {
                dl = true;

                // Stryker disable once Statement : no later pair can clear dl once a pair has set it
                break;
            }
        }

        (zone, tzoff) = dl ? (Zonename(at.Dlname), at.Dldiff) : (Zonename(at.Stname), at.Stdiff);
    }

    // tmfill: the fields of the time in the zone, the date by Tantzen's Algorithm 199.
    private void Tmfill(long abs, long ns)
    {
        long zrel = unchecked(abs + tzoff);
        long t = zrel % Daysec;
        long e = zrel / Daysec;
        if (t < 0)
        {
            t += Daysec;
            e -= 1;
        }

        t += ns / Nsec;
        sec = (int)Mod(t, 60);
        t /= 60;
        min = (int)Mod(t, 60);
        t /= 60;
        hour = (int)Mod(t, 24);
        wday = (int)Mod(e + 4, 7);
        long j = unchecked(((zrel + (2440588 * Daysec)) / Daysec) - 1721119);
        long y = unchecked((4 * j) - 1) / Days400y;
        j = unchecked((4 * j) - 1 - (Days400y * y));
        long d = j / 4;
        j = ((4 * d) + 3) / Days4y;
        d = (4 * d) + 3 - (Days4y * j);
        d = (d + 4) / 4;
        long m = ((5 * d) - 3) / 153;
        d = (5 * d) - 3 - (153 * m);
        d = (d + 5) / 5;
        y = unchecked((100 * y) + j);
        if (m < 10)
        {
            m += 3;
        }
        else
        {
            m -= 9;
            y++;
        }

        // There is no year 0, and for a negative j the day and month are negative too.
        if (y <= 0)
        {
            y--;
        }

        if (m < 0)
        {
            m += 12;
        }

        if (d < 0)
        {
            d += Mdays[m - 1];
        }

        (year, mon, mday, nsec) = (unchecked((int)(y - 1900)), (int)(m - 1), (int)d, (int)Mod(ns, Nsec));
    }

    // A zone: its standard and daylight names and offsets, and the pairs of times its daylight
    // offset holds between.
    public sealed class Tzone(string stname, int stdiff, string dlname, int dldiff)
    {
        public string Stname => stname;

        public string Dlname => dlname;

        public int Stdiff => stdiff;

        public int Dldiff => dldiff;

        public List<int> Dlpairs { get; } = [];
    }
}
