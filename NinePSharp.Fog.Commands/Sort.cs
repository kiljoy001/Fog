using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// sort(1), as 9front's sort.c: each line given a key of bytes, built field by field as its flags
// say, the keys sorted by a radix sort that leaves runs of at most Threshold to a bubble sort, and
// more lines than -l allows sorted in parts kept in temporary files and merged. Its note handler,
// which removes those files at an interrupt, is left out: Fog's handler cannot wait to remove them.
public sealed class Sort
{
    private const int Nline = 100000;
    private const int Nmerge = 10;
    private const int Nfield = 20;
    private const int Threshold = 14;

    private const int Bflag = 1 << 0;
    private const int B1flag = 1 << 1;
    private const int Dflag = 1 << 2;
    private const int Fflag = 1 << 3;
    private const int Gflag = 1 << 4;
    private const int Iflag = 1 << 5;
    private const int Mflag = 1 << 6;
    private const int Nflag = 1 << 7;
    private const int Rflag = 1 << 8;
    private const int Wflag = 1 << 9;

    // lcase, ucase, fold.
    private static readonly int[] Latinmap =
    [
        'à', 'À', 'A', 'á', 'Á', 'A', 'â', 'Â', 'A', 'ä', 'Ä', 'A', 'ã', 'Ã', 'A', 'å', 'Å', 'A',
        'è', 'È', 'E', 'é', 'É', 'E', 'ê', 'Ê', 'E', 'ë', 'Ë', 'E',
        'ì', 'Ì', 'I', 'í', 'Í', 'I', 'î', 'Î', 'I', 'ï', 'Ï', 'I',
        'ò', 'Ò', 'O', 'ó', 'Ó', 'O', 'ô', 'Ô', 'O', 'ö', 'Ö', 'O', 'õ', 'Õ', 'O', 'ø', 'Ø', 'O',
        'ù', 'Ù', 'U', 'ú', 'Ú', 'U', 'û', 'Û', 'U', 'ü', 'Ü', 'U',
        'æ', 'Æ', 'A', 'ð', 'Ð', 'D', 'ñ', 'Ñ', 'N', 'ý', 'Ý', 'Y', 'ç', 'Ç', 'C', 0,
    ];

    private static readonly string[] Month = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private readonly Process process;
    private readonly Field[] field = [.. Enumerable.Range(0, Nfield).Select(_ => new Field())];
    private readonly List<Line> linep = [];
    private string? ofile;
    private string? tname;
    private uint tabchar;
    private bool cflag;
    private bool uflag;
    private bool vflag;
    private int nfield;
    private int nfile;
    private int ntemp;
    private long mline;

    private Sort(Process process) => this.process = process;

    // The states of the decoding of a number into a key.
    private enum Ns
    {
        Start,
        Sign,
        Zero,
        Digit,
        Point,
        Fract,
        Zerofract,
        Exp,
        Expsign,
        Expdigit,
    }

    private enum Dokey
    {
        Plain,
        Dfi,
        Gn,
        M,
        R,
    }

    public static async Task MainAsync(Process process, IReadOnlyList<string> args)
    {
        var sort = new Sort(process);
        string?[] argv = [.. args];
        await sort.DoargsAsync(argv);
        if (sort.vflag)
        {
            await sort.PrintargsAsync();
        }

        for (int i = 1; i < argv.Length; i++)
        {
            if (argv[i] is not { } s)
            {
                continue;
            }

            if (s == "-")
            {
                await sort.DofileAsync(new Biobuf(process, 0));
                continue;
            }

            int f = 0;
            try
            {
                f = await process.OpenAsync(s, NinePConstants.OREAD);
            }
            catch (SyscallException e)
            {
                await sort.FprintAsync($"sort: open {s}: {e.Message}\n");
                await sort.DoneAsync("open");
            }

            await sort.DofileAsync(new Biobuf(process, f));
            await process.CloseAsync(f);
        }

        if (sort.nfile == 0)
        {
            await sort.DofileAsync(new Biobuf(process, 0));
        }

        if (sort.cflag)
        {
            await sort.DoneAsync(null);
        }

        if (sort.vflag)
        {
            await sort.FprintAsync("=========\n");
        }

        int fd = 1;
        if (sort.ofile is not null)
        {
            try
            {
                fd = await process.CreateAsync(sort.ofile, NinePConstants.OWRITE, 0b110_110_110);
            }
            catch (SyscallException e)
            {
                await sort.FprintAsync($"sort: create {sort.ofile}: {e.Message}\n");
                await sort.DoneAsync("create");
            }
        }

        var bbuf = new Biobuf(process, fd);
        if (sort.ntemp > 0)
        {
            await sort.TempoutAsync();
            await sort.MergeoutAsync(bbuf);
        }
        else
        {
            await sort.PrintoutAsync(bbuf);
        }

        await bbuf.BflushAsync();
        await sort.DoneAsync(null);
    }

    // kcmp: the keys compared as far as the shorter goes.
    private static int Kcmp(Key ka, Key kb) => ka.K.AsSpan(0, Math.Min(ka.Klen, kb.Klen)).SequenceCompareTo(kb.K.AsSpan(0, Math.Min(ka.Klen, kb.Klen)));

    private static int Atol(string s) => (int)Libc.Strtol(s, 10, out _);

    // %C of a char: a byte past ASCII is a negative char, no rune, and so the error rune.
    // Stryker disable once Equality : an option's bytes are UTF-8, so no first byte of one is 0x80
    private static string Charfmt(byte c) => c < Utf.RuneSelf ? ((char)c).ToString() : "�";

    private static void Sort4<T>(T[] a, int n, Func<T, Key> key)
    {
        // Stryker disable once Equality : a key ends with the whole line, so no two keys tie unless their lines do, and either sort puts them in one order
        if (n > Threshold)
        {
            Rsort4(a, 0, n, 0, key);
        }
        else
        {
            Bsort4(a, 0, n, 0, key);
        }
    }

    // rsort4: the keys partitioned by their byte b, those that end before it first, each
    // partition's members moved into it by chasing the permutation round, and each partition then
    // sorted on the next byte.
    private static void Rsort4<T>(T[] a, int start, int n, int b, Func<T, Key> key)
    {
        long[] count = new long[257];
        int[] part = new int[257];
        var clist = new List<(long N, int C)>();
        int lowc = 256, higc = 0;
        for (int t = start; t < start + n; t++)
        {
            Key k = key(a[t]);
            if (b >= k.Klen)
            {
                count[256]++;
                continue;
            }

            int c = k.K[b];
            if (count[c]++ == 0)
            {
                lowc = Math.Min(lowc, c);
                higc = Math.Max(higc, c);
            }
        }

        int p = start;
        long ended = count[256];
        part[256] = p;
        p += (int)ended;
        for (int c = lowc; c <= higc; c++)
        {
            if (count[c] != 0)
            {
                clist.Add((count[c], c));
                part[c] = p;
                p += (int)count[c];
            }
        }

        foreach ((_, int c) in clist)
        {
            while (count[c] != 0)
            {
                T t1 = a[part[c]];
                for (; ;)
                {
                    Key k = key(t1);
                    int m = b < k.Klen ? k.K[b] : 256;
                    int u = part[m]++;
                    count[m]--;
                    T u1 = a[u];
                    a[u] = t1;
                    if (m == c)
                    {
                        break;
                    }

                    t1 = u1;
                }
            }
        }

        b++;
        p = start + (int)ended;
        foreach ((long m, _) in clist)
        {
            // Stryker disable once Equality : a key ends with the whole line, so no two keys tie unless their lines do, and either sort puts them in one order
            if (m > Threshold)
            {
                Rsort4(a, p, (int)m, b, key);
            }

            // Stryker disable once Equality : a bubble sort of one key leaves it where it is
            else if (m > 1)
            {
                Bsort4(a, p, (int)m, b, key);
            }

            p += (int)m;
        }
    }

    // bsort4: a bubble sort to pick up the pieces, from byte b on.
    private static void Bsort4<T>(T[] a, int start, int n, int b, Func<T, Key> key)
    {
        int l = start + n;

        // Stryker disable once Arithmetic : from one before the start, the first passes have nothing before them to compare
        for (int j = start + 1; j < l; j++)
        {
            for (int i = j - 1; i >= start && Greater(key(a[i]), key(a[i + 1]), b); i--)
            {
                (a[i], a[i + 1]) = (a[i + 1], a[i]);
            }
        }
    }

    private static bool Greater(Key ka, Key kb, int b)
    {
        int n1 = Math.Min(ka.Klen - b, kb.Klen - b);
        if (n1 <= 0)
        {
            return false;
        }

        int n2 = ka.K[b] - kb.K[b];
        return (n2 == 0 ? ka.K.AsSpan(b, n1).SequenceCompareTo(kb.K.AsSpan(b, n1)) : n2) > 0;
    }

    // The row of latinmap the rune is the lower or upper case of, or -1.
    private static int Latin(int c)
    {
        for (int j = 0; Latinmap[j] != 0; j += 3)
        {
            if (c == Latinmap[j] || c == Latinmap[j + 1])
            {
                return j;
            }
        }

        return -1;
    }

    // dokey_: the field's bytes as they are.
    private static void DokeyPlain(Key k, byte[] line, int lp, int lpe)
    {
        int n = Math.Max(lpe - lp, 0);
        int cl = k.Klen;
        k.Klen = cl + n + 1;
        k.Put(cl, line.AsSpan(lp, n));
        k.Set(cl + n, 0);
    }

    // dokey_r: the field's bytes complemented, and a key that ends in 0xff.
    private static void DokeyR(Key k, byte[] line, int lp, int lpe)
    {
        int n = Math.Max(lpe - lp, 0);
        int cl = k.Klen;
        k.Klen = cl + n + 1;

        // Stryker disable once Equality : the byte past the field is written over by the key's 0xff
        for (int i = 0; i < n; i++)
        {
            k.Set(cl + i, (byte)~line[lp + i]);
        }

        k.Set(cl + n, 0xff);
    }

    // skip: where field n1 begins, past n2 runes more, or with endfield where it ends; the fields
    // are split by the tab character or runs of blanks, and with bflag blanks are passed over
    // first; null past the line's end.
    private int? Skip(byte[] line, int n1, int n2, bool bflag, bool endfield)
    {
        if (endfield && n1 < 0)
        {
            return null;
        }

        int l = 0, c = line[l++], ln = 1;
        uint tc = tabchar;
        if (tc != 0)
        {
            if (tc < Utf.RuneSelf)
            {
                for (int i = n1; i > 0; i--)
                {
                    while (c != tc)
                    {
                        if (c == '\n')
                        {
                            return null;
                        }

                        c = line[l++];
                    }

                    if (!(endfield && i == 1))
                    {
                        c = line[l++];
                    }
                }
            }
            else
            {
                l--;
                l += ln = Utf.CharToRune(out uint r, line.AsSpan(l));
                for (int i = n1; i > 0; i--)
                {
                    while (r != tc)
                    {
                        if (r == '\n')
                        {
                            return null;
                        }

                        l += ln = Utf.CharToRune(out r, line.AsSpan(l));
                    }

                    if (!(endfield && i == 1))
                    {
                        l += ln = Utf.CharToRune(out r, line.AsSpan(l));
                    }
                }

                c = (int)r;
            }
        }
        else
        {
            for (int i = n1; i > 0; i--)
            {
                while (c is ' ' or '\t')
                {
                    c = line[l++];
                }

                while (c is not (' ' or '\t'))
                {
                    if (c == '\n')
                    {
                        return null;
                    }

                    c = line[l++];
                }
            }
        }

        if (bflag)
        {
            while (c is ' ' or '\t')
            {
                c = line[l++];
                ln = 1;
            }
        }

        l -= ln;
        for (int i = n2; i > 0; i--)
        {
            c = line[l];
            if (c < Utf.RuneSelf)
            {
                if (c == '\n')
                {
                    return null;
                }

                l++;
                continue;
            }

            l += Utf.CharToRune(out _, line.AsSpan(l));
        }

        return l;
    }

    // dokey_gn: a number as a key: a sign, a two-byte exponent, the place of the decimal point,
    // and the digits without their leading and trailing zeros, all complemented for -r or a
    // negative number; any zero is 0x20 0.
    private void DokeyGn(Key k, byte[] line, int lp, int lpe, Field f)
    {
        int cl = k.Klen + 3;
        int kp = cl;
        int nzero = 0, exp = 0, dp = 0x4040;
        bool expsign = false;
        bool rflag = (f.Flags & Rflag) != 0;
        Ns state = Ns.Start;
        for (; lp < lpe; lp++)
        {
            int c = line[lp];
            if (c is ' ' or '\t')
            {
                if (state is Ns.Start or Ns.Sign)
                {
                    continue;
                }

                // Stryker disable once Statement : a sign the number cannot take is no digit, point or exponent, and ends it all the same
                break;
            }

            if (c is '+' or '-')
            {
                if (state == Ns.Start)
                {
                    state = Ns.Sign;
                    rflag ^= c == '-';
                    continue;
                }

                if (state == Ns.Exp)
                {
                    state = Ns.Expsign;

                    // Stryker disable once Assignment : the sign of an exponent is read once; a second one ends the number
                    expsign |= c == '-';
                    continue;
                }

                // Stryker disable once Statement : a sign the number cannot take is no digit, point or exponent, and ends it all the same
                break;
            }

            if (c is >= '0' and <= '9')
            {
                switch (state)
                {
                    case Ns.Digit when c == '0':
                    case Ns.Zero or Ns.Start or Ns.Sign or Ns.Digit when c != '0':
                        k.Set(kp++, (byte)(rflag ? ~c : c));
                        cl++;
                        nzero = c == '0' ? nzero + 1 : 0;
                        dp++;
                        state = Ns.Digit;
                        continue;
                    case Ns.Fract:
                    case Ns.Zerofract or Ns.Point when c != '0':
                        k.Set(kp++, (byte)(rflag ? ~c : c));
                        cl++;
                        nzero = c == '0' ? nzero + 1 : 0;
                        state = Ns.Fract;
                        continue;
                    case Ns.Start or Ns.Sign or Ns.Zero:
                        state = Ns.Zero;
                        continue;
                    case Ns.Zerofract or Ns.Point:
                        dp--;
                        state = Ns.Zerofract;
                        continue;
                    case Ns.Expsign or Ns.Exp or Ns.Expdigit:
                        exp = unchecked((exp * 10) + (c - '0'));
                        state = Ns.Expdigit;
                        continue;
                }

                // Stryker disable once Statement : each state a digit can meet is a case above
                break;
            }

            if (c == '.')
            {
                Ns? next = state switch
                {
                    Ns.Start or Ns.Sign => Ns.Point,
                    Ns.Zero => Ns.Zerofract,
                    Ns.Digit => Ns.Fract,
                    _ => null,
                };
                if (next is { } n)
                {
                    state = n;
                    continue;
                }

                // Stryker disable once Statement : a point the number cannot take is no digit or exponent, and ends it all the same
                break;
            }

            if ((f.Flags & Gflag) != 0 && c is 'e' or 'E' && state is Ns.Digit or Ns.Fract)
            {
                state = Ns.Exp;
                continue;
            }

            break;
        }

        switch (state)
        {
            case Ns.Start or Ns.Sign or Ns.Zero or Ns.Zerofract or Ns.Point:
                k.Set(k.Klen, 0x20);

                // Stryker disable once Statement : a key's buffer is new, so the byte after the 0x20 is 0 already
                k.Set(k.Klen + 1, 0);
                k.Klen += 2;
                return;

            // Stryker disable once Logical : in Exp and Expsign no digit of the exponent has come, so it adds 0
            case Ns.Expsign or Ns.Exp or Ns.Expdigit:
                dp = unchecked(dp + (expsign ? -exp : exp));
                break;
        }

        kp -= nzero;
        cl -= nzero;
        k.Set(kp, (byte)(rflag ? 0xff : 0));
        int s = 0x30;
        if (rflag)
        {
            s = 0x10;
            dp = ~dp;
        }

        k.Set(k.Klen, (byte)s);

        // Stryker disable once Bitwise : the byte kept is of bits 8 to 15, which either shift gives alike
        k.Set(k.Klen + 1, (byte)(dp >> 8));
        k.Set(k.Klen + 2, (byte)dp);
        k.Klen = cl + 1;
    }

    // dokey_m: the month the first three letters of the field name, 10 for January to 21 for
    // December, or 9 for none, blanks passed over.
    private void DokeyM(Key k, byte[] line, int lp, int lpe, Field f)
    {
        bool rflag = (f.Flags & Rflag) != 0;
        int cl = k.Klen, kp = cl, pc = 0;
        int[] place = new int[3];
        while (lp < lpe)
        {
            int c = line[lp];
            if (c >= Utf.RuneSelf)
            {
                lp += Utf.CharToRune(out uint r, line.AsSpan(lp));
                c = (int)r;
            }
            else
            {
                lp++;
            }

            if (c < f.Mapto.Length)
            {
                c = f.Mapto[c];
                if (c == 0)
                {
                    continue;
                }
            }

            place[pc++] = c;
            if (pc < 3)
            {
                continue;
            }

            for (c = 11; c >= 0; c--)
            {
                if (Month[c][0] == place[0] && Month[c][1] == place[1] && Month[c][2] == place[2])
                {
                    break;
                }
            }

            c += 10;
            k.Set(kp++, (byte)(rflag ? ~c : c));
            cl++;
            break;
        }

        k.Set(kp, (byte)(rflag ? 0xff : 0));
        k.Klen = cl + 1;
    }

    // dokey_dfi: the field's runes mapped by the field's table, those it maps to 0 left out, and
    // those past it in or out as it maps 0xff; complemented for -r.
    private void DokeyDfi(Key k, byte[] line, int lp, int lpe, Field f)
    {
        int cl = k.Klen, kp = cl;
        bool rflag = (f.Flags & Rflag) != 0;
        byte[] buf = new byte[Utf.UtfMax];
        while (lp < lpe)
        {
            int c = line[lp];
            if (c >= Utf.RuneSelf)
            {
                lp += Utf.CharToRune(out uint r, line.AsSpan(lp));
                c = (int)r;
            }
            else
            {
                lp++;
            }

            // The common case is handled completely by the table.
            if (c != 0 && c < Utf.RuneSelf)
            {
                c = f.Mapto[c];
                if (c != 0)
                {
                    k.Set(kp++, (byte)c);
                    cl++;
                }

                continue;
            }

            // For runes out of its range the table does not do -r, and whether to leave them out
            // is mapto[255]'s.
            if (c != 0 && c < f.Mapto.Length)
            {
                c = f.Mapto[c];
                if (c == 0)
                {
                    continue;
                }
            }
            else if (f.Mapto[^1] == 0)
            {
                continue;
            }

            int n = Utf.RuneToChar(buf, (uint)c);
            for (int i = 0; i < n; i++)
            {
                k.Set(kp++, rflag ? (byte)~buf[i] : buf[i]);
            }

            cl += n;
        }

        k.Klen = cl + 1;
        k.Set(kp, (byte)(rflag ? 0xff : 0));
    }

    private Task FprintAsync(string s) => process.WriteAsync(2, Encoding.UTF8.GetBytes(s)).AsTask();

    // tempfile: the name of temporary file n, in /tmp or -T's directory, by the pid.
    private async Task<string> TempfileAsync(int n)
    {
        string dir = tname ?? "/tmp";
        if (Encoding.UTF8.GetByteCount(dir) >= 100 - 20)
        {
            await FprintAsync($"temp file directory name is too long: {dir}\n");
            await DoneAsync("tdir");
        }

        return Encoding.UTF8.GetString(Libc.Sprint("%s/sort.%.4d.%.4d", [Libc.FmtArg.Of(dir), Libc.FmtArg.Of(process.Pid % 10000), Libc.FmtArg.Of(n)]));
    }

    // done: the temporary files removed, and the status.
    private async Task DoneAsync(string? xs)
    {
        for (int i = 0; i < ntemp; i++)
        {
            try
            {
                await process.RemoveAsync(await TempfileAsync(i));
            }
            catch (SyscallException)
            {
                // remove's failure is not looked at.
            }
        }

        process.Exits(xs);
    }

    private async Task DofileAsync(Biobuf b)
    {
        if (cflag)
        {
            if (await NewlineAsync(b) is not { } ol)
            {
                return;
            }

            for (Line? l; (l = await NewlineAsync(b)) is not null; ol = l)
            {
                int n = Kcmp(ol.Key, l.Key);
                if (n > 0 || (n == 0 && uflag))
                {
                    await FprintAsync("sort: -c file not in sort\n");
                    await DoneAsync("order");
                }
            }

            return;
        }

        for (Line? l; (l = await NewlineAsync(b)) is not null;)
        {
            if (linep.Count >= mline)
            {
                await TempoutAsync();
            }

            linep.Add(l);
        }
    }

    // newline: the next line, from Brdline, or a byte at a time for one too long for its buffer or
    // with no newline at the end, which it is given.
    private async Task<Line?> NewlineAsync(Biobuf b)
    {
        byte[]? p = await b.BrdlineAsync((byte)'\n');
        if (p is null)
        {
            if (b.Blinelen == 0)
            {
                return null;
            }

            var l = new List<byte>();
            for (int c = 0; c != '\n';)
            {
                if ((c = await b.BgetcAsync()) < 0)
                {
                    await FprintAsync("sort: newline added\n");
                    c = '\n';
                }

                l.Add((byte)c);
            }

            p = [.. l];
        }

        var line = new Line(p);
        await BuildkeyAsync(line);
        return line;
    }

    private async Task LineoutAsync(Biobuf b, Line l)
    {
        if (await b.BwriteAsync(l.Bytes) != l.Bytes.Length)
        {
            process.Exits("write");
        }
    }

    // tempout: the lines so far sorted into the next temporary file.
    private async Task TempoutAsync()
    {
        Line[] lines = [.. linep];
        Sort4(lines, lines.Length, l => l.Key);
        string tf = await TempfileAsync(ntemp);
        ntemp++;
        int f = 0;
        try
        {
            f = await process.CreateAsync(tf, NinePConstants.OWRITE, 0b110_110_110);
        }
        catch (SyscallException e)
        {
            await FprintAsync($"sort: create {tf}: {e.Message}\n");
            await DoneAsync("create");
        }

        var tb = new Biobuf(process, f);
        foreach (Line l in lines)
        {
            await LineoutAsync(tb, l);
        }

        linep.Clear();
        await tb.BflushAsync();

        // Stryker disable once Statement : a descriptor left open changes nothing sort writes
        await process.CloseAsync(f);
    }

    // mergeout: the temporary files merged Nmerge at a time into another, until the last of them
    // are merged into the output.
    private async Task MergeoutAsync(Biobuf b)
    {
        for (int i = 0, n; i < ntemp; i += n)
        {
            n = ntemp - i;

            // Stryker disable once Equality : Nmerge files merged through one more temporary file give the lines they would merged at once
            if (n <= Nmerge)
            {
                await MergefilesAsync(i, n, b);
                continue;
            }

            string tf = await TempfileAsync(ntemp);
            ntemp++;
            int f = 0;
            try
            {
                f = await process.CreateAsync(tf, NinePConstants.OWRITE, 0b110_110_110);
            }
            catch (SyscallException e)
            {
                await FprintAsync($"sort: create {tf}: {e.Message}\n");
                await DoneAsync("create");
            }

            var tb = new Biobuf(process, f);
            n = Nmerge;
            await MergefilesAsync(i, n, tb);
            await tb.BflushAsync();

            // Stryker disable once Statement : a descriptor left open changes nothing sort writes
            await process.CloseAsync(f);
        }
    }

    // mergefiles: n temporary files from t merged: the one whose line sorts first gives lines
    // until another's does.
    private async Task MergefilesAsync(int t, int n, Biobuf b)
    {
        var mmp = new Merge[n];
        var mp = new List<Merge>();
        int nn = 0;
        for (int i = 0; i < n; i++)
        {
            string tf = await TempfileAsync(t + i);
            int f = 0;
            try
            {
                f = await process.OpenAsync(tf, NinePConstants.OREAD);
            }
            catch (SyscallException e)
            {
                await FprintAsync($"sort: reopen {tf}: {e.Message}\n");
                await DoneAsync("open");
            }

            var m = new Merge(f, new Biobuf(process, f));
            mp.Add(m);
            mmp[nn] = m;
            if (await NewlineAsync(m.B) is not { } l)
            {
                continue;
            }

            nn++;
            m.Line = l;
        }

        Key? ok = null;
        for (; ;)
        {
            Sort4(mmp, nn, m => m.Line!.Key);
            Merge m = mmp[0];
            if (nn == 0)
            {
                break;
            }

            for (; ;)
            {
                Line l = m.Line!;
                if (!(uflag && ok is not null && Kcmp(ok, l.Key) == 0))
                {
                    await LineoutAsync(b, l);
                    ok = l.Key;
                }

                if (await NewlineAsync(m.B) is not { } next)
                {
                    nn--;
                    mmp[0] = mmp[nn];
                    break;
                }

                m.Line = next;
                if (nn > 1 && Kcmp(mmp[0].Line!.Key, mmp[1].Line!.Key) > 0)
                {
                    break;
                }
            }
        }

        foreach (Merge m in mp)
        {
            await process.CloseAsync(m.Fd);
        }
    }

    private async Task PrintoutAsync(Biobuf b)
    {
        Line[] lines = [.. linep];
        Sort4(lines, lines.Length, l => l.Key);
        Key? ok = null;
        foreach (Line l in lines)
        {
            if (uflag && ok is not null && Kcmp(ok, l.Key) == 0)
            {
                continue;
            }

            await LineoutAsync(b, l);
            ok = l.Key;
        }
    }

    private async Task SetfieldAsync(int n, byte c)
    {
        Field f = field[n];
        int flag = c switch
        {
            (byte)'b' => Bflag,
            (byte)'d' => Dflag,
            (byte)'f' => Fflag,
            (byte)'g' => Gflag,
            (byte)'i' => Iflag,
            (byte)'M' => Mflag,
            (byte)'n' => Nflag,
            (byte)'r' => Rflag,
            (byte)'w' => Wflag,
            _ => 0,
        };
        if (flag == 0)
        {
            await FprintAsync($"sort: unknown option: field.{Charfmt(c)}\n");
            await DoneAsync("option");
        }

        f.Flags |= flag;
    }

    // dofield: a field's position, the field and then after a '.' the character, each less its
    // offset, and the flags after them.
    private async Task DofieldAsync(byte[] s, Action<int> n1, Action<int> n2, int off1, int off2)
    {
        int p = 0;
        byte c = s[p++];
        if (c is >= (byte)'0' and <= (byte)'9')
        {
            int n = 0;
            for (; c is >= (byte)'0' and <= (byte)'9'; c = s[p++])
            {
                n = unchecked((n * 10) + (c - '0'));
            }

            n -= off1;
            if (n < 0)
            {
                await FprintAsync("sort: field offset must be positive\n");
                await DoneAsync("option");
            }

            n1(n);
        }

        if (c == '.')
        {
            c = s[p++];
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                int n = 0;
                for (; c is >= (byte)'0' and <= (byte)'9'; c = s[p++])
                {
                    n = unchecked((n * 10) + (c - '0'));
                }

                n -= off2;
                if (n < 0)
                {
                    await FprintAsync("sort: character offset must be positive\n");
                    await DoneAsync("option");
                }

                n2(n);
            }
        }

        for (; c != 0; c = s[p++])
        {
            await SetfieldAsync(nfield, c);
        }
    }

    private async Task PrintargsAsync()
    {
        var o = new StringBuilder("sort");
        for (int i = 0; i <= nfield; i++)
        {
            Field f = field[i];
            string prefix = " -";
            if (i != 0)
            {
                o.Append(f.Beg1 >= 0 ? $" +{f.Beg1}" : " +*");
                o.Append(f.Beg2 >= 0 ? $".{f.Beg2}" : ".*");
                o.Append((f.Flags & B1flag) != 0 ? "b" : string.Empty);
                o.Append(f.End1 >= 0 ? $" -{f.End1}" : " -*");
                o.Append(f.End2 >= 0 ? $".{f.End2}" : ".*");
                prefix = string.Empty;
            }

            foreach ((int flag, char c) in new[] { (Bflag, 'b'), (Dflag, 'd'), (Fflag, 'f'), (Gflag, 'g'), (Iflag, 'i'), (Mflag, 'M'), (Nflag, 'n'), (Rflag, 'r'), (Wflag, 'w') })
            {
                o.Append((f.Flags & flag) != 0 ? $"{prefix}{c}" : string.Empty);
            }
        }

        o.Append(cflag ? " -c" : string.Empty);
        o.Append(uflag ? " -u" : string.Empty);
        o.Append(ofile is not null ? $" -o {ofile}" : string.Empty);
        o.Append(mline != Nline ? $" -l {mline}" : string.Empty);
        await FprintAsync(o.Append('\n').ToString());
    }

    private async Task NewfieldAsync()
    {
        int n = nfield + 1;
        if (n >= Nfield)
        {
            await FprintAsync("sort: too many fields specified\n");
            await DoneAsync("option");
        }

        nfield = n;
        field[n].Beg1 = field[n].Beg2 = field[n].End1 = field[n].End2 = -1;
    }

    // An option's value: the rest of its argument, or the next, which is then taken from the files.
    private string? Optarg(string?[] argv, ref int i, byte[] s, ref int p)
    {
        string? value;
        if (s[p] == 0)
        {
            value = ++i < argv.Length ? argv[i] : null;
            if (i < argv.Length)
            {
                argv[i] = null;
            }
        }
        else
        {
            value = Encoding.UTF8.GetString(s, p, Array.IndexOf(s, (byte)0, p) - p);
        }

        p = Array.IndexOf(s, (byte)0, p);
        return value;
    }

    private async Task DoargsAsync(string?[] argv)
    {
        bool hadplus = false;
        mline = Nline;
        int i;
        for (i = 1; i < argv.Length; i++)
        {
            byte[] s = [.. Encoding.UTF8.GetBytes(argv[i]!), 0];
            int p = 0;
            byte c = s[p++];
            if (c == '-')
            {
                c = s[p];
                if (c == 0)
                {
                    // A forced end of the options.
                    break;
                }

                argv[i] = null;
                if (c == '.' || c is >= (byte)'0' and <= (byte)'9')
                {
                    if (!hadplus)
                    {
                        await NewfieldAsync();
                    }

                    Field f = field[nfield];
                    await DofieldAsync(s[p..], n => f.End1 = n, n => f.End2 = n, 0, 0);
                    hadplus = false;
                    continue;
                }

                while ((c = s[p++]) != 0)
                {
                    switch ((char)c)
                    {
                        case '-':
                            // The end of the options: the arguments after it are files, but none
                            // are counted.
                            i = argv.Length;
                            continue;
                        case 'T':
                            tname = Optarg(argv, ref i, s, ref p) ?? tname;
                            break;
                        case 'o':
                            ofile = Optarg(argv, ref i, s, ref p) ?? ofile;
                            break;
                        case 'k':
                            if (Optarg(argv, ref i, s, ref p) is not { } key)
                            {
                                break;
                            }

                            await NewfieldAsync();
                            byte[] kb = [.. Encoding.UTF8.GetBytes(key), 0];
                            int q = Array.IndexOf(kb, (byte)',');
                            Field f = field[nfield];
                            await DofieldAsync(q < 0 ? kb : [.. kb[..q], 0], n => f.Beg1 = n, n => f.Beg2 = n, 1, 1);
                            if ((f.Flags & Bflag) != 0)
                            {
                                f.Flags = (f.Flags | B1flag) & ~Bflag;
                            }

                            if (q >= 0)
                            {
                                await DofieldAsync(kb[(q + 1)..], n => f.End1 = n, n => f.End2 = n, 1, 0);
                                if (f.End2 <= 0)
                                {
                                    f.End1++;
                                }
                            }

                            hadplus = false;
                            break;
                        case 't':
                            if (s[p] == 0)
                            {
                                if (++i < argv.Length)
                                {
                                    Utf.CharToRune(out tabchar, [.. Encoding.UTF8.GetBytes(argv[i]!), 0]);
                                    argv[i] = null;
                                }
                            }
                            else
                            {
                                p += Utf.CharToRune(out tabchar, s.AsSpan(p));
                            }

                            if (tabchar == '\n')
                            {
                                await FprintAsync("aw come on, rob\n");
                                await DoneAsync("rob");
                            }

                            break;
                        case 'c':
                            cflag = true;
                            break;
                        case 'u':
                            uflag = true;
                            break;
                        case 'v':
                            vflag = true;
                            break;
                        case 'l':
                            string? l = Optarg(argv, ref i, s, ref p);
                            mline = l is null ? mline : Atol(l);
                            break;
                        case 'M' or 'b' or 'd' or 'f' or 'g' or 'i' or 'n' or 'r' or 'w':
                            if (nfield > 0)
                            {
                                await FprintAsync("sort: global field set after -k\n");
                            }

                            await SetfieldAsync(0, c);
                            break;
                        case 'm':
                            // Option m is silently ignored but required by posix.
                            break;
                        default:
                            await FprintAsync($"sort: unknown option: -{Charfmt(c)}\n");
                            await DoneAsync("option");
                            break;
                    }
                }

                continue;
            }

            if (c == '+')
            {
                argv[i] = null;
                c = s[p];
                if (c == '.' || c is >= (byte)'0' and <= (byte)'9')
                {
                    await NewfieldAsync();
                    Field f = field[nfield];
                    await DofieldAsync(s[p..], n => f.Beg1 = n, n => f.Beg2 = n, 0, 0);
                    if ((f.Flags & Bflag) != 0)
                    {
                        f.Flags = (f.Flags | B1flag) & ~Bflag;
                    }

                    hadplus = true;
                    continue;
                }

                await FprintAsync($"sort: unknown option: +{Charfmt(c)}\n");
                await DoneAsync("option");
            }

            nfile++;
        }

        for (i = 0; i <= nfield; i++)
        {
            Field f = field[i];

            // The global options apply to fields that specify none.
            if (f.Flags == 0)
            {
                f.Flags = field[0].Flags | ((field[0].Flags & Bflag) != 0 ? B1flag : 0);
            }

            // Of -d, -f, -i and -w, with -r or not, sort.c takes all but -w with -r alone.
            int flags = f.Flags & ~(Bflag | B1flag);

            // Stryker disable once Block : a field's key is built by dokey_ unless it is told otherwise
            if (flags == 0)
            {
                f.Dokey = Dokey.Plain;
            }
            else if (flags == Rflag)
            {
                f.Dokey = Dokey.R;
            }
            else if ((flags & ~(Gflag | Nflag | Rflag)) == 0)
            {
                f.Dokey = Dokey.Gn;
            }
            else if (flags == Mflag || flags == (Mflag | Rflag))
            {
                f.Dokey = Dokey.M;
                await MakemapmAsync(f);
            }
            else if ((flags & ~(Dflag | Fflag | Iflag | Rflag | Wflag)) == 0 && flags != (Rflag | Wflag))
            {
                f.Dokey = Dokey.Dfi;
                await MakemapdAsync(f);
            }
            else
            {
                await FprintAsync($"sort: illegal combination of flags: {f.Flags:x}\n");
                await DoneAsync("option");
            }
        }

        if (nfile > 1 && cflag)
        {
            await FprintAsync("sort: -c can have at most one input file\n");
            await DoneAsync("option");
        }
    }

    private async Task BuildkeyAsync(Line l)
    {
        int ll = l.Bytes.Length - 1;
        var k = new Key();
        int cl = 0;
        for (int i = 1; i <= nfield; i++)
        {
            Field f = field[i];
            int lp = Skip(l.Bytes, f.Beg1, f.Beg2, (f.Flags & B1flag) != 0, false) ?? ll;
            int lpe = Skip(l.Bytes, f.End1, f.End2, (f.Flags & Bflag) != 0, true) ?? ll;
            k.Klen = cl;
            Dokeyf(f, k, l.Bytes, lp, lpe);
            cl = k.Klen;
        }

        // The global comparisons.
        if (!(uflag && cl > 0))
        {
            k.Klen = cl;
            Dokeyf(field[0], k, l.Bytes, 0, ll);
            cl = k.Klen;
        }

        l.Key = k;
        k.Klen = cl;
        if (vflag)
        {
            await process.WriteAsync(2, l.Bytes);
            var o = new StringBuilder();
            for (int i = 0; i < k.Klen; i++)
            {
                o.Append($" {k.K[i]:x2}");
                o.Append(k.K[i] is 0x00 or 0xff ? "\n" : string.Empty);
            }

            await FprintAsync(o.ToString());
        }
    }

    private void Dokeyf(Field f, Key k, byte[] line, int lp, int lpe)
    {
        switch (f.Dokey)
        {
            case Dokey.Plain:
                DokeyPlain(k, line, lp, lpe);
                break;
            case Dokey.R:
                DokeyR(k, line, lp, lpe);
                break;
            case Dokey.Gn:
                DokeyGn(k, line, lp, lpe, f);
                break;
            case Dokey.M:
                DokeyM(k, line, lp, lpe, f);
                break;
            default:
                DokeyDfi(k, line, lp, lpe, f);
                break;
        }
    }

    // makemapm: letters upper case, blanks left out, and the rest 1.
    private async Task MakemapmAsync(Field f)
    {
        for (int i = 0; i < f.Mapto.Length; i++)
        {
            int c = i is ' ' or '\t' ? 0 : i is >= 'a' and <= 'z' ? i + ('A' - 'a') : i is >= 'A' and <= 'Z' ? i : 1;
            f.Mapto[i] = (byte)c;
        }

        await DumpmapAsync(f);
    }

    // makemapd: each byte as -i, -w and -d leave it out, -f folds it, and -r complements it.
    private async Task MakemapdAsync(Field f)
    {
        for (int i = 0; i < f.Mapto.Length; i++)
        {
            int c = i;
            if ((f.Flags & Iflag) != 0 && (c < 0x20 || c > 0x7e))
            {
                c = -1;
            }

            if ((f.Flags & Wflag) != 0 && c is ' ' or '\t')
            {
                c = -1;
            }

            // Stryker disable once Equality : a NUL, kept or taken out, maps to 0
            if ((f.Flags & Dflag) != 0 && c >= 0 && !(c is ' ' or '\t' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')) && Latin(c) < 0)
            {
                c = -1;
            }

            // Stryker disable once Equality : a NUL folds to itself
            if ((f.Flags & Fflag) != 0 && c >= 0)
            {
                c = c is >= 'a' and <= 'z' ? c + ('A' - 'a') : c;
                c = Latin(c) is int j and >= 0 ? Latinmap[j + 2] : c;
            }

            // Stryker disable once Equality : past the NUL at 0, no rune maps to 0
            if ((f.Flags & Rflag) != 0 && c >= 0 && i > 0 && i < Utf.RuneSelf)
            {
                c = ~c & 0xff;
            }

            f.Mapto[i] = (byte)Math.Max(c, 0);
        }

        await DumpmapAsync(f);
    }

    private async Task DumpmapAsync(Field f)
    {
        if (!vflag)
        {
            return;
        }

        var o = new StringBuilder();
        for (int i = 0; i < f.Mapto.Length; i++)
        {
            o.Append((i & 15) == 0 ? "\t" : string.Empty);
            o.Append($" {f.Mapto[i]:x2}");
            o.Append((i & 15) == 15 ? "\n" : string.Empty);
        }

        await FprintAsync(o.ToString());
    }

    // A key: its bytes, as many as Klen says, the array growing as they are set.
    private sealed class Key
    {
        public byte[] K { get; private set; } = new byte[16];

        public int Klen { get; set; }

        public void Set(int i, byte b)
        {
            Grow(i + 1);
            K[i] = b;
        }

        public void Put(int i, ReadOnlySpan<byte> bytes)
        {
            Grow(i + bytes.Length);
            bytes.CopyTo(K.AsSpan(i));
        }

        private void Grow(int n)
        {
            // Stryker disable once Equality,Arithmetic : how much room a key is given past what it holds changes no key
            if (n > K.Length)
            {
                byte[] k = new byte[Math.Max(n, K.Length * 2)];
                K.CopyTo(k, 0);
                K = k;
            }
        }
    }

    // A line, which always ends in a newline, and its key.
    private sealed class Line(byte[] bytes)
    {
        public byte[] Bytes => bytes;

        public Key Key { get; set; } = new();
    }

    private sealed class Merge(int fd, Biobuf b)
    {
        public int Fd => fd;

        public Biobuf B => b;

        public Line? Line { get; set; }
    }

    private sealed class Field
    {
        public int Beg1 { get; set; }

        public int Beg2 { get; set; }

        public int End1 { get; set; }

        public int End2 { get; set; }

        public int Flags { get; set; }

        public byte[] Mapto { get; } = new byte[256];

        public Dokey Dokey { get; set; }
    }
}
