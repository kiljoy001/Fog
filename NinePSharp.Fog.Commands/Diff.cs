using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// diff(1), as 9front's diff: Stone's longest common subsequence of the files' lines, hashed, its
// matches checked against the lines themselves, and the changes printed as ed's commands, or with
// -e, -f, -n, -c, -a and -u in their own forms; -b and -w ignore blanks, and -r follows directories
// down. Its lines are C strings of at most MAXLINELEN-1 bytes, read again by the offsets their
// lengths add up to.
public sealed class Diff
{
    private const int Maxpathlen = 1024;
    private const int Maxlinelen = 4096;
    private const int Halflong = 16;

    // The lines of context shown.
    private const int Lines = 3;

    private readonly Process process;
    private readonly Biobuf stdout;
    private char mode;
    private int bflag;
    private bool rflag;
    private bool mflag;
    private bool anychange;

    private Diff(Process process) => (this.process, stdout) = (process, new Biobuf(process, 1));

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var diff = new Diff(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'e' or 'f' or 'n' or 'c' or 'a' or 'u':
                    diff.mode = c;
                    break;
                case 'w':
                    diff.bflag = 2;
                    break;
                case 'b':
                    diff.bflag = 1;
                    break;
                case 'r':
                    diff.rflag = true;
                    break;
                case 'm':
                    diff.mflag = true;
                    break;
                default:
                    await diff.UsageAsync();
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count < 2)
        {
            await diff.UsageAsync();
        }

        Stat tsb = await diff.DirstatAsync(args[^1]) ?? await diff.SysfatalAsync<Stat>($"can't stat {args[^1]}");
        if (args.Count > 2)
        {
            if (!Directory(tsb))
            {
                await diff.SysfatalAsync<Stat>($"not directory: {args[^1]}");
            }

            diff.mflag = true;
        }
        else
        {
            Stat fsb = await diff.DirstatAsync(args[0]) ?? await diff.SysfatalAsync<Stat>($"can't stat {args[0]}");
            if (Directory(fsb) && Directory(tsb))
            {
                diff.mflag = true;
            }
        }

        for (int i = 0; i < args.Count - 1; i++)
        {
            await diff.DiffAsync(args[i], args[^1], 0);
        }

        await diff.ExitsAsync(diff.anychange ? "some" : string.Empty);
    }

    private static bool Directory(Stat s) => (s.Qid.Type & QidType.QTDIR) != 0;

    private static bool RegularFile(Stat s) => s.Type == 'M' && !Directory(s);

    private static bool Isspace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';

    // The string of a C buffer: up to its first NUL.
    private static int Strlen(byte[] buf)
    {
        int n = Array.IndexOf(buf, (byte)0);
        return n < 0 ? buf.Length : n;
    }

    private static bool Strcmp(byte[] a, byte[] b) => a.AsSpan(0, Strlen(a)).SequenceEqual(b.AsSpan(0, Strlen(b)));

    private static int Strcmp(string a, string b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    // %s of a C string: its runes as chartorune reads them, a byte that begins none an error rune,
    // up to a rune cut short at its end.
    private static byte[] Fmtcpy(byte[] buf)
    {
        var o = new List<byte>();
        byte[] rune = new byte[Utf.UtfMax];
        for (int m = 0, me = Strlen(buf); m < me;)
        {
            if (buf[m] < Utf.RuneSelf)
            {
                o.Add(buf[m++]);
                continue;
            }

            if (me - m < Utf.UtfMax && !Utf.FullRune(buf.AsSpan(m, me - m)))
            {
                break;
            }

            m += Utf.CharToRune(out uint r, buf.AsSpan(m, me - m));
            o.AddRange(rune.AsSpan(0, Utf.RuneToChar(rune, r)).ToArray());
        }

        return [.. o];
    }

    // readline: a line, its newline kept, of up to nbuf-1 bytes, the rest of a longer one dropped;
    // -1 at the end of input.
    private static int Readline(Bfile bp, byte[] buf, int nbuf)
    {
        int c, p = 0, e = nbuf - 1;
        do
        {
            c = bp.Bgetc();
            if (c < 0)
            {
                if (p == 0)
                {
                    return -1;
                }

                break;
            }

            buf[p++] = (byte)c;
            if (c == '\n')
            {
                break;
            }
        }
        while (p < e);
        buf[p] = 0;
        if (c != '\n' && c >= 0)
        {
            do
            {
                c = bp.Bgetc();
            }
            while (c >= 0 && c != '\n');
        }

        return p;
    }

    // shellsort, CACM #201, by value and then serial.
    private static void Sort(Line[] f, int a, int n)
    {
        int m = 0;
        for (int i = 1; i <= n; i *= 2)
        {
            m = (2 * i) - 1;
        }

        for (m /= 2; m != 0; m /= 2)
        {
            int k = a + (n - m);
            for (int j = a + 1; j <= k; j++)
            {
                int ai = j;
                int aim = ai + m;
                do
                {
                    if (f[aim].Value > f[ai].Value || (f[aim].Value == f[ai].Value && f[aim].Serial > f[ai].Serial))
                    {
                        break;
                    }

                    (f[ai], f[aim]) = (f[aim], f[ai]);
                    aim = ai;
                    ai -= m;
                }
                while (ai > a && aim >= ai);
            }
        }
    }

    private static void Unsort(Line[] f, int a, int l, int[] b)
    {
        int[] t = new int[l + 1];
        for (int i = 1; i <= l; i++)
        {
            t[f[a + i].Serial] = f[a + i].Value;
        }

        Array.Copy(t, 1, b, 1, l);
    }

    private static void Equiv(Line[] fa, int a, int n, Line[] fb, int b, int m, int[] c)
    {
        int i = 1, j = 1;
        while (i <= n && j <= m)
        {
            if (fa[a + i].Value < fb[b + j].Value)
            {
                fa[a + i++].Value = 0;
            }
            else if (fa[a + i].Value == fb[b + j].Value)
            {
                fa[a + i++].Value = j;
            }
            else
            {
                j++;
            }
        }

        while (i <= n)
        {
            fa[a + i++].Value = 0;
        }

        fb[b + m + 1].Value = 0;
        j = 0;
        while (++j <= m)
        {
            c[j] = -fb[b + j].Serial;
            while (fb[b + j + 1].Value == fb[b + j].Value)
            {
                j++;
                c[j] = fb[b + j].Serial;
            }
        }

        c[j] = -1;
    }

    private static int Search(D d, int[] c, int k, int y)
    {
        // A quick look for the typical case.
        if (d.Clist[c[k]].Y < y)
        {
            return k + 1;
        }

        int i = 0, j = k + 1, l;
        while ((l = (i + j) / 2) > i)
        {
            int t = d.Clist[c[l]].Y;
            if (t > y)
            {
                j = l;
            }
            else if (t < y)
            {
                i = l;
            }
            else
            {
                return l;
            }
        }

        return l + 1;
    }

    private static int Newcand(D d, int x, int y, int pred)
    {
        d.Clist.Add(new Cand(x, y, pred));
        return d.Clist.Count - 1;
    }

    // stone: the k-candidates, each the earliest line of the second file a common subsequence of
    // length k reaches, chained to the k-1 one before it.
    private static int Stone(D d, int[] a, int n, int[] b, int[] c)
    {
        int k = 0;
        c[0] = Newcand(d, 0, 0, 0);
        for (int i = 1; i <= n; i++)
        {
            int j = a[i];
            if (j == 0)
            {
                continue;
            }

            int y = -b[j];
            int oldl = 0;
            int oldc = c[0];
            do
            {
                if (y <= d.Clist[oldc].Y)
                {
                    continue;
                }

                int l = Search(d, c, k, y);
                if (l != oldl + 1)
                {
                    oldc = c[l - 1];
                }

                if (l <= k)
                {
                    if (d.Clist[c[l]].Y <= y)
                    {
                        continue;
                    }

                    int tc = c[l];
                    c[l] = Newcand(d, i, y, oldc);
                    oldc = tc;
                    oldl = l;
                }
                else
                {
                    c[l] = Newcand(d, i, y, oldc);
                    k++;
                    break;
                }
            }
            while ((y = b[++j]) > 0);
        }

        return k;
    }

    private static void Prune(D d)
    {
        for (d.Pref = 0; d.Pref < d.Len[0] && d.Pref < d.Len[1] && d.File[0][d.Pref + 1].Value == d.File[1][d.Pref + 1].Value; d.Pref++)
        {
        }

        for (d.Suff = 0; d.Suff < d.Len[0] - d.Pref && d.Suff < d.Len[1] - d.Pref && d.File[0][d.Len[0] - d.Suff].Value == d.File[1][d.Len[1] - d.Suff].Value; d.Suff++)
        {
        }

        for (int j = 0; j < 2; j++)
        {
            d.Slen[j] = d.Len[j] - d.Pref - d.Suff;
            for (int i = 0; i <= d.Slen[j]; i++)
            {
                d.File[j][d.Pref + i].Serial = i;
            }
        }
    }

    private static void Unravel(D d, int p)
    {
        for (int i = 0; i <= d.Len[0]; i++)
        {
            d.J[i] = i <= d.Pref ? i : i > d.Len[0] - d.Suff ? i + d.Len[1] - d.Len[0] : 0;
        }

        for (Cand q = d.Clist[p]; q.Y != 0; q = d.Clist[q.Pred])
        {
            d.J[q.X + d.Pref] = q.Y + d.Pref;
        }
    }

    // cmp: whether the files' bytes differ.
    private static bool Cmp(Bfile b1, Bfile b2) => !b1.Data.AsSpan().SequenceEqual(b2.Data);

    private async Task UsageAsync()
    {
        await process.WriteAsync(2, "usage: diff [-abcefmnrw] file1 ... file2\n"u8.ToArray());
        process.Exits("usage");
    }

    // sysfatal: the message, and the output buffered so far flushed as diff exits.
    private async Task<T> SysfatalAsync<T>(string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"diff: {message}\n"));
        await ExitsAsync(message);
        return default!;
    }

    private async Task ExitsAsync(string status)
    {
        await stdout.BflushAsync();
        process.Exits(status);
    }

    private Task Bprint(string s) => stdout.BwriteAsync(Encoding.UTF8.GetBytes(s));

    private async Task<Stat?> DirstatAsync(string file)
    {
        try
        {
            return await process.StatAsync(file);
        }
        catch (SyscallException e)
        {
            Errstr.Set(process, e.Message);
            return null;
        }
    }

    // statfile: the file, or for standard input and a file that is neither regular nor a
    // directory, a copy of what it holds, and its Dir.
    private async Task<(Bfile? Copy, string File, Stat Sb)> StatfileAsync(string file)
    {
        Stat? dir = await DirstatAsync(file);
        if (dir is null)
        {
            Stat? input = null;
            if (file == "-")
            {
                try
                {
                    input = await process.FStatAsync(0);
                }
                catch (SyscallException e)
                {
                    Errstr.Set(process, e.Message);
                }
            }

            if (input is null)
            {
                await SysfatalAsync<Stat>($"cannot stat {file}: {Errstr.Get(process)}");
            }

            return await MktmpfileAsync(0, file);
        }

        if (!RegularFile(dir.Value) && !Directory(dir.Value))
        {
            int input = 0;
            try
            {
                input = await process.OpenAsync(file, NinePConstants.OREAD);
            }
            catch (SyscallException e)
            {
                await SysfatalAsync<Stat>($"cannot open {file}: {e.Message}");
            }

            (Bfile? Copy, string File, Stat Sb) copy = await MktmpfileAsync(input, file);
            await process.CloseAsync(input);
            return copy;
        }

        return (null, file, dir.Value);
    }

    // mktmpfile: what the descriptor holds, kept as diff's temporary file keeps it, a regular file.
    private async Task<(Bfile? Copy, string File, Stat Sb)> MktmpfileAsync(int input, string file)
    {
        var data = new List<byte>();
        try
        {
            for (ReadOnlyMemory<byte> read; !(read = await process.ReadAsync(input, 8192)).IsEmpty;)
            {
                data.AddRange(read.ToArray());
            }
        }
        catch (SyscallException e)
        {
            await SysfatalAsync<Stat>($"cannot read/write {file}: {e.Message}");
        }

        return (new Bfile([.. data]), file, new Stat(0, 'M', 0, new Qid(QidType.QTFILE, 0, 0), 0, 0, 0, (ulong)data.Count, string.Empty, string.Empty, string.Empty, string.Empty));
    }

    private async Task<string> MkpathnameAsync(string path, string name)
    {
        if (Encoding.UTF8.GetByteCount(path) + Encoding.UTF8.GetByteCount(name) > Maxpathlen)
        {
            await SysfatalAsync<int>($"pathname {path}/{name} too long");
        }

        return $"{path}/{name}";
    }

    private async Task DiffAsync(string f, string t, int level)
    {
        (Bfile? fc, string fp, Stat fsb) = await StatfileAsync(f);
        (Bfile? tc, string tp, Stat tsb) = await StatfileAsync(t);
        if (Directory(fsb) && Directory(tsb))
        {
            if (rflag || level == 0)
            {
                await DiffdirAsync(fp, tp, level);
            }
            else
            {
                await Bprint($"Common subdirectories: {fp} and {tp}\n");
            }
        }
        else if (RegularFile(fsb) && RegularFile(tsb))
        {
            await DiffregAsync(fc, fp, f, tc, tp, t);
        }
        else if (RegularFile(fsb))
        {
            string p = f[(f.LastIndexOf('/') + 1)..];
            await DiffregAsync(fc, fp, f, null, await MkpathnameAsync(tp, p), t);
        }
        else
        {
            string p = t[(t.LastIndexOf('/') + 1)..];
            await DiffregAsync(null, await MkpathnameAsync(fp, p), f, tc, tp, t);
        }
    }

    // scandir: the names in the directory, in strcmp's order, or none, with a message, for one
    // that cannot be opened.
    private async Task<List<string>> ScandirAsync(string name)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync(name, NinePConstants.OREAD);
        }
        catch (SyscallException e)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"diff: can't open {name}: {e.Message}\n"));
            return [];
        }

        var names = new List<string>();
        try
        {
            names.AddRange((await process.DirReadAsync(fd)).Select(d => d.Name));
        }
        catch (SyscallException)
        {
            // dirreadall's error is a directory of nothing.
        }

        await process.CloseAsync(fd);
        names.Sort(Strcmp);
        return names;
    }

    private async Task DiffdirAsync(string f, string t, int level)
    {
        List<string> df = await ScandirAsync(f), dt = await ScandirAsync(t);
        int i = 0, j = 0;
        while (i < df.Count || j < dt.Count)
        {
            string? from = i < df.Count ? df[i] : null;
            string? to = j < dt.Count ? dt[j] : null;
            if (from is "." or "..")
            {
                i++;
                continue;
            }

            if (to is "." or "..")
            {
                j++;
                continue;
            }

            int res = from is null ? 1 : to is null ? -1 : Strcmp(from, to);
            if (res < 0)
            {
                if (mode is '\0' or 'n')
                {
                    await Bprint($"Only in {f}: {from}\n");
                }

                i++;
                continue;
            }

            if (res > 0)
            {
                if (mode is '\0' or 'n')
                {
                    await Bprint($"Only in {t}: {to}\n");
                }

                j++;
                continue;
            }

            await DiffAsync(await MkpathnameAsync(f, from!), await MkpathnameAsync(t, to!), level + 1);
            i++;
            j++;
        }
    }

    private async Task DiffregAsync(Bfile? fc, string f, string fo, Bfile? tc, string t, string to)
    {
        var d = new D();
        await CalcdiffAsync(d, fc, f, fo, tc, t, to);
        await OutputAsync(d);
    }

    private async Task CalcdiffAsync(D d, Bfile? fc, string f, string fo, Bfile? tc, string t, string to)
    {
        Bfile b0 = await PrepareAsync(d, 0, fc, f, fo);
        Bfile b1 = await PrepareAsync(d, 1, tc, t, to);
        if (d.Binary)
        {
            d.Bindiff = Cmp(b0, b1);
            return;
        }

        Prune(d);
        Sort(d.File[0], d.Pref, d.Slen[0]);
        Sort(d.File[1], d.Pref, d.Slen[1]);
        int[] member = new int[d.Slen[1] + 2];
        Equiv(d.File[0], d.Pref, d.Slen[0], d.File[1], d.Pref, d.Slen[1], member);
        int[] @class = new int[d.Slen[0] + 2];
        Unsort(d.File[0], d.Pref, d.Slen[0], @class);
        int[] klist = new int[d.Slen[0] + 2];
        int k = Stone(d, @class, d.Slen[0], member, klist);
        d.J = new int[d.Len[0] + 2];
        Unravel(d, klist[k]);
        d.Ixold = new long[d.Len[0] + 2];
        d.Ixnew = new long[d.Len[1] + 2];
        b0.Bseek(0);
        b1.Bseek(0);
        Check(d, b0, b1);
    }

    // prepare: the file opened, or its copy, and unless either file is found binary, its lines'
    // hashes, up to the first that hashes to 0, as the end does.
    private async Task<Bfile> PrepareAsync(D d, int i, Bfile? copy, string arg, string orig)
    {
        if (i == 0)
        {
            d.File1 = orig;
        }
        else
        {
            d.File2 = orig;
        }

        Bfile bp = copy ?? await BopenAsync(arg);
        d.Input[i] = bp;
        if (d.Binary)
        {
            return bp;
        }

        int nbytes = Math.Min(bp.Data.Length, Math.Min(1024, Maxlinelen));
        for (int cp = 0; cp < nbytes - Utf.UtfMax;)
        {
            // A heuristic for a binary file in the brave new Unicode world.
            cp += Utf.CharToRune(out uint r, bp.Data.AsSpan(cp, nbytes - cp));
            if (r == 0 || (r > 0x7f && r <= 0xa0))
            {
                d.Binary = true;
                return bp;
            }
        }

        var p = new List<Line> { default };
        byte[] buf = new byte[Maxlinelen];
        for (int h; (h = Readhash(bp, buf, buf.Length)) != 0;)
        {
            p.Add(new Line(0, h));
        }

        d.Len[i] = p.Count - 1;
        p.AddRange([default, default]);
        d.File[i] = [.. p];
        return bp;
    }

    private async Task<Bfile> BopenAsync(string arg)
    {
        var data = new List<byte>();
        try
        {
            int fd = await process.OpenAsync(arg, NinePConstants.OREAD);
            for (ReadOnlyMemory<byte> read; !(read = await process.ReadAsync(fd, Biobuf.Bsize)).IsEmpty;)
            {
                data.AddRange(read.ToArray());
            }

            await process.CloseAsync(fd);
        }
        catch (SyscallException e)
        {
            await SysfatalAsync<int>($"cannot open {arg}: {e.Message}");
        }

        return new Bfile([.. data]);
    }

    // readhash: the line's bytes, signed chars, summed shifted by 7 more each, the shift kept
    // under 16, in a 32-bit long, and the halves of the sum added; with -b a run of blanks
    // shifts once and adds nothing, and with -w blanks are passed over.
    private int Readhash(Bfile bp, byte[] buf, int nbuf)
    {
        int sum = 1, shift = 0;
        int len = Readline(bp, buf, nbuf);
        if (len == -1)
        {
            return 0;
        }

        bool space = false;
        for (int p = 0; p < len; p++)
        {
            if (bflag != 0 && Isspace(buf[p]))
            {
                space = bflag == 1;
                continue;
            }

            if (space)
            {
                shift += 7;
                space = false;
            }

            sum = unchecked(sum + ((sbyte)buf[p] << (shift &= Halflong - 1)));
            shift += 7;
        }

        sum = (sum & ((1 << Halflong) - 1)) + (sum >> Halflong);
        return (short)(sum & ((1 << Halflong) - 1)) + (short)(sum >> Halflong);
    }

    // squishspace: with -b each run of blanks before a byte one space, with -w none.
    private int Squishspace(byte[] buf)
    {
        int p = 0;
        bool space = false;
        for (int q = 0; buf[q] != 0; q++)
        {
            if (Isspace(buf[q]))
            {
                space = true;
                continue;
            }

            if (space && bflag == 1)
            {
                buf[p++] = (byte)' ';
            }

            space = false;
            buf[p++] = buf[q];
        }

        buf[p] = 0;
        return p;
    }

    // check: each match found by the hashes of all but the last line of the first file, broken
    // where the lines differ; and the offset of each line, by the lengths readline gives.
    private void Check(D d, Bfile bf, Bfile bt)
    {
        byte[] fbuf = new byte[Maxlinelen], tbuf = new byte[Maxlinelen];
        int f, t, tlen = 0;
        d.Ixold[0] = 0;
        d.Ixnew[0] = 0;
        for (f = t = 1; f < d.Len[0]; f++)
        {
            int flen = Readline(bf, fbuf, fbuf.Length);
            d.Ixold[f] = d.Ixold[f - 1] + flen;
            if (d.J[f] == 0)
            {
                continue;
            }

            do
            {
                tlen = Readline(bt, tbuf, tbuf.Length);
                d.Ixnew[t] = d.Ixnew[t - 1] + tlen;
            }
            while (t++ < d.J[f]);
            if (bflag != 0)
            {
                flen = Squishspace(fbuf);
                tlen = Squishspace(tbuf);
            }

            if (flen != tlen || !Strcmp(fbuf, tbuf))
            {
                d.J[f] = 0;
            }
        }

        for (; t < d.Len[1]; t++)
        {
            tlen = Readline(bt, tbuf, tbuf.Length);
            d.Ixnew[t] = d.Ixnew[t - 1] + tlen;
        }
    }

    private async Task OutputAsync(D d)
    {
        if (d.Bindiff)
        {
            // print, not Bprint: before what the buffer holds.
            await process.WriteAsync(1, Encoding.UTF8.GetBytes($"binary files {d.File1} {d.File2} differ\n"));
        }

        if (d.Binary)
        {
            return;
        }

        int m = d.Len[0], i0, i1, j0, j1;
        d.J[0] = 0;
        d.J[m + 1] = d.Len[1] + 1;
        if (mode != 'e')
        {
            for (i0 = 1; i0 <= m; i0 = i1 + 1)
            {
                while (i0 <= m && d.J[i0] == d.J[i0 - 1] + 1)
                {
                    i0++;
                }

                j0 = d.J[i0 - 1] + 1;
                i1 = i0 - 1;
                while (i1 < m && d.J[i1 + 1] == 0)
                {
                    i1++;
                }

                j1 = d.J[i1 + 1] - 1;
                d.J[i1] = j1;
                await ChangeAsync(d, i0, i1, j0, j1);
            }
        }
        else
        {
            for (i0 = m; i0 >= 1; i0 = i1 - 1)
            {
                while (i0 >= 1 && d.J[i0] == d.J[i0 + 1] - 1 && d.J[i0] != 0)
                {
                    i0--;
                }

                j0 = d.J[i0 + 1] - 1;
                i1 = i0 + 1;
                while (i1 > 1 && d.J[i1 - 1] == 0)
                {
                    i1--;
                }

                j1 = d.J[i1 - 1] + 1;
                d.J[i1] = j1;
                await ChangeAsync(d, i1, i0, j1, j0);
            }
        }

        if (m == 0)
        {
            await ChangeAsync(d, 1, 0, 1, d.Len[1]);
        }

        await FlushchangesAsync(d);
    }

    private Task Range(long a, long b, string separator)
        => Bprint(a < b ? $"{a}{separator}{b}" : $"{Math.Min(a, b)}");

    // fetch: lines a to b, of those the file has, each after s, and a last one without its newline
    // noted.
    private async Task FetchAsync(D d, long[] f, long a, long b, Bfile bp, string s)
    {
        byte[] buf = new byte[Maxlinelen];
        a = Math.Max(a, 1);
        int maxb = bp == d.Input[0] ? d.Len[0] : d.Len[1];
        b = Math.Min(b, maxb);
        if (a > maxb)
        {
            return;
        }

        bp.Bseek(f[a - 1]);
        while (a++ <= b)
        {
            int len = Readline(bp, buf, buf.Length);
            await stdout.BwriteAsync(Encoding.UTF8.GetBytes(s));
            await stdout.BwriteAsync(Fmtcpy(buf));
            if (len <= 0 || buf[len - 1] != '\n')
            {
                await Bprint("\n\\ No newline at end of file\n");
            }
        }
    }

    private async Task ChangeAsync(D df, int a, int b, int c, int d)
    {
        if (a > b && c > d)
        {
            return;
        }

        anychange = true;
        if (mflag && !df.Firstchange)
        {
            await Bprint($"diff {(mode != '\0' ? $"-{mode} " : string.Empty)}{df.File1} {df.File2}\n");
            df.Firstchange = true;
        }

        char verb = a > b ? 'a' : c > d ? 'd' : 'c';
        switch (mode)
        {
            case 'e':
                await Range(a, b, ",");
                await Bprint($"{verb}");
                break;
            case '\0':
                await Range(a, b, ",");
                await Bprint($"{verb}");
                await Range(c, d, ",");
                break;
            case 'n':
                await Bprint($"{df.File1}:");
                await Range(a, b, ",");
                await Bprint($" {verb} {df.File2}:");
                await Range(c, d, ",");
                break;
            case 'f':
                await Bprint($"{verb}");
                await Range(a, b, " ");
                break;
            default:
                df.Changes.Add(new Change(a, b, c, d));
                return;
        }

        await Bprint("\n");
        if (mode is '\0' or 'n')
        {
            await FetchAsync(df, df.Ixold, a, b, df.Input[0]!, "< ");
            if (a <= b && c <= d)
            {
                await Bprint("---\n");
            }
        }

        await FetchAsync(df, df.Ixnew, c, d, df.Input[1]!, mode is '\0' or 'n' ? "> " : string.Empty);
        if (mode is not ('\0' or 'n') && c <= d)
        {
            await Bprint(".\n");
        }
    }

    // changeset: the end of the run of changes from i whose contexts meet.
    private int Changeset(D d, int i)
    {
        while (i + 1 < d.Changes.Count && d.Changes[i].Oldy + 1 + (2 * Lines) > d.Changes[i + 1].Oldx)
        {
            i++;
        }

        return i + 1;
    }

    // flushchanges: the changes of -c, -a and -u, each run of them whose contexts meet printed with
    // the lines about them.
    private async Task FlushchangesAsync(D df)
    {
        bool hdr = false;
        for (int i = 0; i < df.Changes.Count;)
        {
            int j = Changeset(df, i);
            long a = Math.Max(df.Changes[i].Oldx - Lines, 1);
            long b = Math.Min(df.Changes[j - 1].Oldy + Lines, df.Len[0]);
            long c = Math.Max(df.Changes[i].Newx - Lines, 1);
            long d = Math.Min(df.Changes[j - 1].Newy + Lines, df.Len[1]);
            if (mode == 'a')
            {
                (a, b, c, d, j) = (1, df.Len[0], 1, df.Len[1], df.Changes.Count);
            }

            if (mode == 'u')
            {
                if (!hdr)
                {
                    await Bprint($"--- {df.File1}\n+++ {df.File2}\n");
                    hdr = true;
                }

                await Bprint($"@@ -{a},{b - a + 1} +{c},{d - c + 1} @@\n");
            }
            else
            {
                await Bprint($"{df.File1}:");
                await Range(a, b, ",");
                await Bprint($" - {df.File2}:");
                await Range(c, d, ",");
                await Bprint("\n");
            }

            long at = a;
            for (; i < j; i++)
            {
                await FetchAsync(df, df.Ixold, at, df.Changes[i].Oldx - 1, df.Input[0]!, mode == 'u' ? " " : "  ");
                await FetchAsync(df, df.Ixold, df.Changes[i].Oldx, df.Changes[i].Oldy, df.Input[0]!, mode == 'u' ? "-" : "- ");
                await FetchAsync(df, df.Ixnew, df.Changes[i].Newx, df.Changes[i].Newy, df.Input[1]!, mode == 'u' ? "+" : "+ ");
                at = df.Changes[i].Oldy + 1;
            }

            await FetchAsync(df, df.Ixold, at, b, df.Input[0]!, mode == 'u' ? " " : "  ");
        }
    }

    private record struct Line(int Serial, int Value);

    private readonly record struct Cand(int X, int Y, int Pred);

    private readonly record struct Change(int Oldx, int Oldy, int Newx, int Newy);

    // A file's bytes, read through as a Biobuf would read them, from where Bseek puts it.
    private sealed class Bfile(byte[] data)
    {
        private int at;

        public byte[] Data => data;

        public int Bgetc() => at < data.Length ? data[at++] : Biobuf.Beof;

        public void Bseek(long offset) => at = (int)Math.Min(offset, data.Length);
    }

    // A comparison of two files.
    private sealed class D
    {
        public Line[][] File { get; } = [[], []];

        public int[] Len { get; } = new int[2];

        public int[] Slen { get; } = new int[2];

        public int Pref { get; set; }

        public int Suff { get; set; }

        public List<Cand> Clist { get; } = [];

        public int[] J { get; set; } = [];

        public long[] Ixold { get; set; } = [];

        public long[] Ixnew { get; set; } = [];

        public string File1 { get; set; } = string.Empty;

        public string File2 { get; set; } = string.Empty;

        public Bfile?[] Input { get; } = new Bfile?[2];

        public bool Binary { get; set; }

        public bool Bindiff { get; set; }

        public bool Firstchange { get; set; }

        public List<Change> Changes { get; } = [];
    }
}
