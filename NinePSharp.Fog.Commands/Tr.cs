using System.Text;
using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// tr(1), as 9front's tr.c: its specifications read rune by rune from their UTF-8, its input read
// as readrune reads it, a rune taking the bytes fullrune says it needs, and its output buffered.
public sealed class Tr
{
    private const long Runemax = Utf.RuneMax;
    private const int Buf = 4096;

    private static readonly byte[] Bits = [1, 2, 4, 8, 16, 32, 64, 128];

    private readonly Process process;
    private readonly string argv0;
    private readonly byte[] f = new byte[(Runemax + 1) / 8];
    private readonly byte[] t = new byte[(Runemax + 1) / 8];
    private readonly byte[] wbuf = new byte[Buf];
    private readonly byte[] rbuf = new byte[Buf];
    private readonly Pcb pfrom = new();
    private readonly Pcb pto = new();
    private int wptr;
    private int ri;
    private int rn;
    private bool cflag;
    private bool dflag;
    private bool sflag;

    private Tr(Process process, string argv0) => (this.process, this.argv0) = (process, argv0);

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var tr = new Tr(process, argv[0]);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 's':
                    tr.sflag = true;
                    break;
                case 'd':
                    tr.dflag = true;
                    break;
                case 'c':
                    tr.cflag = true;
                    break;
                default:
                    await tr.UsageAsync();
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();

        // The arguments as exec leaves them, each ending in a NUL and the next after it.
        byte[] strings = [.. argv.SelectMany(a => Encoding.UTF8.GetBytes(a).Append((byte)0))];
        int first = argv.Take(argv.Count - args.Count).Sum(a => Encoding.UTF8.GetByteCount(a) + 1);
        if (args.Count > 0)
        {
            tr.pfrom.Init(strings, first);
        }

        if (args.Count > 1)
        {
            tr.pto.Init(strings, first + Encoding.UTF8.GetByteCount(args[0]) + 1);
        }

        if (args.Count > 2)
        {
            await tr.UsageAsync();
        }

        if (tr.dflag)
        {
            if (tr.sflag ? args.Count != 2 : args.Count != 1)
            {
                await tr.UsageAsync();
            }

            await tr.DeleteAsync();
        }
        else
        {
            if (args.Count != 2)
            {
                await tr.UsageAsync();
            }

            await (tr.cflag ? tr.ComplementAsync() : tr.TranslitAsync());
        }

        process.Exits(null);
    }

    private static void Setbit(byte[] a, long c) => a[c / 8] |= Bits[c & 7];

    private static void Clearbit(byte[] a, long c) => a[c / 8] &= (byte)~Bits[c & 7];

    private static bool Bitset(byte[] a, long c) => (a[c / 8] & Bits[c & 7]) != 0;

    private async Task UsageAsync()
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"usage: {argv0} [-cds] [string1 [string2]]\n"));
        process.Exits("usage");
    }

    private async Task<T> SysfatalAsync<T>(string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: {message}\n"));
        process.Exits(message);
        return default!;
    }

    private async Task DeleteAsync()
    {
        long c;
        if (cflag)
        {
            Array.Fill(f, (byte)0xff);
            while ((c = await CanonAsync(pfrom)) >= 0)
            {
                Clearbit(f, c);
            }
        }
        else
        {
            while ((c = await CanonAsync(pfrom)) >= 0)
            {
                Setbit(f, c);
            }
        }

        if (sflag)
        {
            while ((c = await CanonAsync(pto)) >= 0)
            {
                Setbit(t, c);
            }
        }

        long last = Runemax + 1;
        while (await ReadruneAsync() is long r)
        {
            if (!Bitset(f, r) && (r != last || !Bitset(t, r)))
            {
                last = r;
                await WriteruneAsync((uint)r);
            }
        }

        await WflushAsync();
    }

    private async Task ComplementAsync()
    {
        long from, to, lastc = 0, high = 0;
        while ((from = await CanonAsync(pfrom)) >= 0)
        {
            high = Math.Max(high, from);
            Setbit(f, from);
        }

        while ((to = await CanonAsync(pto)) > 0)
        {
            high = Math.Max(high, to);
            Setbit(t, to);
        }

        pto.Rewind();
        uint[] p = new uint[high + 1];
        for (long i = 0; i <= high; i++)
        {
            if (!Bitset(f, i))
            {
                if ((to = await CanonAsync(pto)) < 0)
                {
                    to = lastc;
                }
                else
                {
                    lastc = to;
                }

                p[i] = (uint)to;
            }
            else
            {
                p[i] = (uint)i;
            }
        }

        await PutAsync(p, high, to);
    }

    private async Task TranslitAsync()
    {
        long from, to, lastc = 0, high = 0;
        while ((from = await CanonAsync(pfrom)) >= 0)
        {
            high = Math.Max(high, from);
        }

        pfrom.Rewind();
        uint[] p = new uint[high + 1];
        for (long i = 0; i <= high; i++)
        {
            p[i] = (uint)i;
        }

        while ((from = await CanonAsync(pfrom)) >= 0)
        {
            if ((to = await CanonAsync(pto)) < 0)
            {
                to = lastc;
            }
            else
            {
                lastc = to;
            }

            if (Bitset(f, from) && p[from] != to)
            {
                await SysfatalAsync<bool>("ambiguous translation");
            }

            Setbit(f, from);
            p[from] = (uint)to;
            Setbit(t, to);
        }

        while ((to = await CanonAsync(pto)) >= 0)
        {
            Setbit(t, to);
        }

        await PutAsync(p, high, null);
    }

    // The input through p, a rune past it becoming beyond, or left as it is when there is none;
    // with -s, a run of one rune that is in string2 put once.
    private async Task PutAsync(uint[] p, long high, long? beyond)
    {
        long lastc = Runemax + 1;
        while (await ReadruneAsync() is long r)
        {
            long from = r <= high ? p[r] : beyond ?? r;
            if (!sflag || from != lastc || !Bitset(t, from))
            {
                lastc = from;
                await WriteruneAsync((uint)from);
            }
        }

        await WflushAsync();
    }

    // The next rune of standard input, or null at its end, output flushed before each read.
    private async Task<long?> ReadruneAsync()
    {
        int j = ri;
        for (; ;)
        {
            if (ri >= rn)
            {
                await WflushAsync();
                if (j != ri)
                {
                    Array.Copy(rbuf, j, rbuf, 0, rn - j);
                }

                ri = rn - j;
                ReadOnlyMemory<byte> got;
                try
                {
                    got = await process.ReadAsync(0, Buf - ri);
                }
                catch (SyscallException error)
                {
                    return await SysfatalAsync<long?>($"read error: {error.Message}");
                }

                if (got.IsEmpty)
                {
                    return null;
                }

                got.Span.CopyTo(rbuf.AsSpan(ri));
                j = 0;
                rn = got.Length + ri;
            }

            ri++;
            if (Utf.FullRune(rbuf.AsSpan(j, ri - j)))
            {
                break;
            }
        }

        Utf.CharToRune(out uint rune, rbuf.AsSpan(j, ri - j));
        return rune;
    }

    private async Task WriteruneAsync(uint r)
    {
        byte[] buf = new byte[Utf.UtfMax];
        int n = Utf.RuneToChar(buf, r);
        if (wptr + n >= Buf)
        {
            await WflushAsync();
        }

        buf.AsSpan(0, n).CopyTo(wbuf.AsSpan(wptr));
        wptr += n;
    }

    private async Task WflushAsync()
    {
        if (wptr > 0)
        {
            try
            {
                await process.WriteAsync(1, wbuf.AsMemory(0, wptr).ToArray());
            }
            catch (SyscallException error)
            {
                await SysfatalAsync<bool>($"write error: {error.Message}");
            }
        }

        wptr = 0;
    }

    // The rune at s and the index past it: a backslash taking up to four hex digits after an x,
    // or up to three octal ones, or the next rune as itself.
    private async Task<(uint Rune, int Next)> GetruneAsync(Pcb b, int s)
    {
        s += Utf.CharToRune(out uint r, b.From(s));
        if (r != '\\' || b[s] == 0)
        {
            return (r, s);
        }

        long n = 0;
        if (b[s] == 'x')
        {
            s++;
            for (int i = 0; i < 4; i++)
            {
                int save = s;
                s += Utf.CharToRune(out r, b.From(s));
                if (r is >= '0' and <= '9')
                {
                    n = (16 * n) + r - '0';
                }
                else if (r is >= 'a' and <= 'f')
                {
                    n = (16 * n) + r - 'a' + 10;
                }
                else if (r is >= 'A' and <= 'F')
                {
                    n = (16 * n) + r - 'A' + 10;
                }
                else
                {
                    return (i == 0 ? 'x' : (uint)n, save);
                }
            }
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                int save = s;
                s += Utf.CharToRune(out r, b.From(s));
                if (r is >= '0' and <= '7')
                {
                    n = (8 * n) + r - '0';
                }
                else
                {
                    return i == 0 ? (r, s) : ((uint)n, save);
                }
            }

            if (n > 0x0ff)
            {
                await SysfatalAsync<bool>("character > 0377");
            }
        }

        return ((uint)n, s);
    }

    // The next rune of the specification, a range a-b giving each from a to b, or -1 at its end.
    private async Task<long> CanonAsync(Pcb p)
    {
        if (p.Final >= 0)
        {
            if (p.Last < p.Final)
            {
                return ++p.Last;
            }

            p.Final = -1;
        }

        if (p[p.Current] == 0)
        {
            return -1;
        }

        uint r;
        if (p[p.Current] == '-' && p.Last >= 0 && p[p.Current + 1] != 0)
        {
            (r, p.Current) = await GetruneAsync(p, p.Current + 1);
            if (r < p.Last)
            {
                await SysfatalAsync<bool>("invalid range specification");
            }

            if (r > p.Last)
            {
                p.Final = r;
                return ++p.Last;
            }
        }

        (r, p.Current) = await GetruneAsync(p, p.Current);
        p.Last = r;
        return p.Last;
    }

    // A specification, a C string among the arguments, and where its parse is: a parse past its
    // NUL goes on into the next argument, as tr.c's does, and finds NULs past the last.
    private sealed class Pcb
    {
        private byte[] strings = [];
        private int start;

        public int Current { get; set; }

        public long Last { get; set; } = -1;

        public long Final { get; set; } = -1;

        public byte this[int i] => i < strings.Length ? strings[i] : (byte)0;

        // The bytes from i on, as many as a rune can take.
        public ReadOnlySpan<byte> From(int i)
        {
            Span<byte> bytes = new byte[Utf.UtfMax];
            for (int k = 0; k < bytes.Length; k++)
            {
                bytes[k] = this[i + k];
            }

            return bytes;
        }

        public void Init(byte[] strings, int start)
        {
            (this.strings, this.start) = (strings, start);
            Rewind();
        }

        public void Rewind()
        {
            Current = start;
            Last = Final = -1;
        }
    }
}
