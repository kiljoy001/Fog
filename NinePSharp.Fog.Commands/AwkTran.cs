using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// awk's tran.c, its symbol tables and the values of cells, and lib.c, its records and fields.
public sealed partial class Awk
{
    private readonly List<Cell> fldtab = [];
    private AwkArray symtab = null!;
    private Cell fsCell = null!;
    private Cell rsCell = null!;
    private Cell ofsCell = null!;
    private Cell orsCell = null!;
    private Cell convfmtCell = null!;
    private Cell filenameCell = null!;
    private Cell subsepCell = null!;
    private Cell nfloc = null!;
    private Cell nrloc = null!;
    private Cell fnrloc = null!;
    private Cell rstartloc = null!;
    private Cell rlengthloc = null!;
    private Cell argvloc = null!;
    private Cell argcloc = null!;
    private Cell literal0 = null!;
    private Node nullnode = null!;
    private Bio? infile;
    private string inputFS = " ";
    private int nfields = Maxfld;
    private bool donefld;
    private bool donerec;
    private int lastfld;
    private int argno = 1;
    private bool firsttime = true;

    private static int Hash(string s, int n)
    {
        uint hashval = 0;
        foreach (char c in s)
        {
            hashval = unchecked((uint)(sbyte)(byte)c + (31 * hashval));
        }

        return (int)(hashval % (uint)n);
    }

    private static AwkArray Makesymtab(int n) => new(n);

    private static void Rehash(AwkArray tp)
    {
        int nsz = Growtab * tp.Size;
        var np = new Cell?[nsz];
        for (int i = 0; i < tp.Size; i++)
        {
            for (Cell? cp = tp.Tab[i], op; cp is not null; cp = op)
            {
                op = cp.Cnext;
                int nh = Hash(cp.Nval!, nsz);
                cp.Cnext = np[nh];
                np[nh] = cp;
            }
        }

        (tp.Tab, tp.Size) = (np, nsz);
    }

    private static Cell? Lookup(string s, AwkArray tp)
    {
        for (Cell? p = tp.Tab[Hash(s, tp.Size)]; p is not null; p = p.Cnext)
        {
            if (p.Nval == s)
            {
                return p;
            }
        }

        return null;
    }

    private static void Freeelem(Cell ap, string s)
    {
        AwkArray tp = ap.Arr!;
        int h = Hash(s, tp.Size);
        for (Cell? p = tp.Tab[h], prev = null; p is not null; prev = p, p = p.Cnext)
        {
            if (p.Nval == s)
            {
                if (prev is null)
                {
                    tp.Tab[h] = p.Cnext;
                }
                else
                {
                    prev.Cnext = p.Cnext;
                }

                tp.Nelemt--;
                return;
            }
        }
    }

    // strtod and strtoll as to_number reads them: a number, and whether all of s, but for blanks
    // after it, is one; with rem, only where it stops.
    private static bool IsFloat(string s, out double f, out int end)
    {
        f = Libc.Strtod(s, out end);
        if (end == 0 || double.IsInfinity(f) || double.IsNaN(f))
        {
            return false;
        }

        int q = s.IndexOf('0', StringComparison.Ordinal);
        return !(f == 0.0 && (q < 0 || q > end));
    }

    private static bool ToNumber(string s, out double f, out int rem)
    {
        long v = Libc.Strtoll(s, 0, out int p);
        f = v;
        rem = p;
        if (p < s.Length && s[p] is '.' or 'E' or 'I' or 'N' or 'e' or 'i' or 'n')
        {
            bool fl = IsFloat(s, out f, out rem);
            return fl;
        }

        if (p == 0)
        {
            return false;
        }

        int q = s.IndexOf('0', StringComparison.Ordinal);
        return !(v == 0 && (q < 0 || q > p));
    }

    private static bool ToNumber(string s, out double f)
    {
        if (!ToNumber(s, out f, out int p))
        {
            return false;
        }

        for (; p < s.Length; p++)
        {
            if (s[p] is not (' ' or '\t' or '\n' or '\f' or '\r' or '\v'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Isclvar(string s)
    {
        if (s.Length == 0 || !(Isalpha(s[0]) || s[0] == '_'))
        {
            return false;
        }

        int i = 1;
        while (i < s.Length && (Isalnum(s[i]) || s[i] == '_'))
        {
            i++;
        }

        return i < s.Length && s[i] == '=' && !(i + 1 < s.Length && s[i + 1] == '=');
    }

    private static int Atoi(string s) => Libc.Strtol(s, 10, out _);

    private void Syminit()
    {
        literal0 = Setsymtab("0", "0", 0.0, Num | Str | Con | Dontfree, symtab);
        Cell nullloc = Setsymtab("$zero&null", string.Empty, 0.0, Num | Str | Con | Dontfree, symtab);
        nullnode = Celltonode(nullloc, Ccon);
        fsCell = Setsymtab("FS", " ", 0.0, Str | Dontfree, symtab);
        rsCell = Setsymtab("RS", "\n", 0.0, Str | Dontfree, symtab);
        ofsCell = Setsymtab("OFS", " ", 0.0, Str | Dontfree, symtab);
        orsCell = Setsymtab("ORS", "\n", 0.0, Str | Dontfree, symtab);
        Setsymtab("OFMT", "%.6g", 0.0, Str | Dontfree, symtab);
        convfmtCell = Setsymtab("CONVFMT", "%.6g", 0.0, Str | Dontfree, symtab);
        filenameCell = Setsymtab("FILENAME", string.Empty, 0.0, Str | Dontfree, symtab);
        nfloc = Setsymtab("NF", string.Empty, 0.0, Num, symtab);
        nrloc = Setsymtab("NR", string.Empty, 0.0, Num, symtab);
        fnrloc = Setsymtab("FNR", string.Empty, 0.0, Num, symtab);
        subsepCell = Setsymtab("SUBSEP", "\x1c", 0.0, Str | Dontfree, symtab);
        rstartloc = Setsymtab("RSTART", string.Empty, 0.0, Num, symtab);
        rlengthloc = Setsymtab("RLENGTH", string.Empty, 0.0, Num, symtab);
        Setsymtab("SYMTAB", string.Empty, 0.0, Arr, symtab).Arr = symtab;
    }

    private void Arginit(List<string> av)
    {
        argcloc = Setsymtab("ARGC", string.Empty, av.Count, Num, symtab);
        argvloc = Setsymtab("ARGV", string.Empty, 0.0, Arr, symtab);
        AwkArray ap = Makesymtab(Nsymtab);
        argvloc.Arr = ap;
        for (int i = 0; i < av.Count; i++)
        {
            string temp = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ToNumber(av[i], out double f))
            {
                Setsymtab(temp, av[i], f, Str | Num, ap);
            }
            else
            {
                Setsymtab(temp, av[i], 0.0, Str, ap);
            }
        }
    }

    // envinit: ENVIRON, of /env but for functions, each value as getenv gives it.
    private async Task<AwkArray> EnvinitAsync()
    {
        AwkArray envtab = Makesymtab(Nsymtab);
        int fd;
        try
        {
            fd = await process.OpenAsync("/env", NinePConstants.OREAD);
        }
        catch (SyscallException)
        {
            return envtab;
        }

        IReadOnlyList<Stat> dir;
        try
        {
            dir = await process.DirReadAsync(fd);
        }
        catch (SyscallException)
        {
            dir = [];
        }

        foreach (Stat d in dir)
        {
            string k = Lat(Encoding.UTF8.GetBytes(d.Name));
            if (k.StartsWith("fn#", StringComparison.Ordinal) || await GetenvAsync(d.Name) is not { } v)
            {
                continue;
            }

            if (ToNumber(v, out double f))
            {
                Setsymtab(k, v, f, Str | Num, envtab);
            }
            else
            {
                Setsymtab(k, v, 0.0, Str, envtab);
            }
        }

        await process.CloseAsync(fd);
        return envtab;
    }

    // getenv: the variable's file, its NULs but the last made spaces.
    private async Task<string?> GetenvAsync(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Contains('/', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(name) >= 100 - 5)
        {
            return null;
        }

        var s = new List<byte>();
        try
        {
            int f = await process.OpenAsync($"/env/{name}", NinePConstants.OREAD | NinePConstants.OCEXEC);
            try
            {
                for (ReadOnlyMemory<byte> r; !(r = await process.ReadAsync(f, 100)).IsEmpty;)
                {
                    s.AddRange(r.ToArray());
                }
            }
            finally
            {
                await process.CloseAsync(f);
            }
        }
        catch (SyscallException)
        {
            return null;
        }

        int n = s.Count;
        while (n > 0 && s[n - 1] == 0)
        {
            n--;
        }

        return Lat([.. s.Take(n).Select(b => b == 0 ? (byte)' ' : b)]);
    }

    // setsymtab: the cell of the name, made if there is none.
    private Cell Setsymtab(string n, string s, double f, int t, AwkArray tp)
    {
        if (Lookup(n, tp) is { } found)
        {
            return found;
        }

        var p = new Cell { Nval = n, Fval = f, Csub = Cunk, Ctype = Ocell };
        if (tp == symtab && n == "ENVIRON" && !safe)
        {
            p.Arr = EnvinitAsync().GetAwaiter().GetResult();
            p.Tval = Arr;
        }
        else
        {
            p.Sval = Cstr(s);
            p.Tval = t | (p.Sval.Length == 0 ? Dontfree : 0);
        }

        if (++tp.Nelemt > Fulltab * tp.Size)
        {
            Rehash(tp);
        }

        int h = Hash(n, tp.Size);
        p.Cnext = tp.Tab[h];
        tp.Tab[h] = p;
        return p;
    }

    private async Task FunnyvarAsync(Cell vp, string rw)
    {
        if ((vp.Tval & Arr) != 0)
        {
            await FatalAsync($"can't {rw} {vp.Nval}; it's an array name.");
        }

        if ((vp.Tval & Fcn) != 0)
        {
            await FatalAsync($"can't {rw} {vp.Nval}; it's a function.");
        }

        await WarningAsync($"funny variable {vp.GetHashCode():x}: n={vp.Nval} s=\"{vp.Sval}\" f={Sprint("%g", A(vp.Fval))} t={Convert.ToString(vp.Tval, 8)}");
    }

    private async Task<double> SetfvalAsync(Cell vp, double f)
    {
        if ((vp.Tval & (Num | Str)) == 0)
        {
            await FunnyvarAsync(vp, "assign to");
        }

        if ((vp.Tval & Fld) != 0)
        {
            donerec = false;
            int fldno = Atoi(vp.Nval!);
            if (fldno > nfloc.Fval)
            {
                Newfld(fldno);
            }
        }
        else if ((vp.Tval & Rec) != 0)
        {
            (donefld, donerec) = (false, true);
        }

        vp.Tval = (vp.Tval & ~Str) | Num;
        return vp.Fval = f;
    }

    private async Task<string> SetsvalAsync(Cell vp, string s)
    {
        if ((vp.Tval & (Num | Str)) == 0)
        {
            await FunnyvarAsync(vp, "assign to");
        }

        if ((vp.Tval & Fld) != 0)
        {
            donerec = false;
            int fldno = Atoi(vp.Nval!);
            if (fldno > nfloc.Fval)
            {
                Newfld(fldno);
            }
        }
        else if ((vp.Tval & Rec) != 0)
        {
            (donefld, donerec) = (false, true);
        }

        string t = Cstr(s);
        vp.Tval = (vp.Tval & ~Num) | Str;
        vp.Tval = t.Length != 0 ? vp.Tval & ~Dontfree : vp.Tval | Dontfree;
        return vp.Sval = t;
    }

    private async Task<double> GetfvalAsync(Cell vp)
    {
        if ((vp.Tval & (Num | Str)) == 0)
        {
            await FunnyvarAsync(vp, "read value of");
        }

        if ((vp.Tval & Fld) != 0 && !donefld)
        {
            await FldbldAsync();
        }
        else if ((vp.Tval & Rec) != 0 && !donerec)
        {
            await RecbldAsync();
        }

        if ((vp.Tval & Num) == 0)
        {
            vp.Fval = 0;
            if (ToNumber(vp.Sval, out double f))
            {
                vp.Fval = f;
                vp.Tval |= (vp.Tval & Con) == 0 ? Num : 0;
            }
            else
            {
                vp.Fval = f;
            }
        }

        return vp.Fval;
    }

    private async Task<string> GetsvalAsync(Cell vp)
    {
        if ((vp.Tval & (Num | Str)) == 0)
        {
            await FunnyvarAsync(vp, "read value of");
        }

        if ((vp.Tval & Fld) != 0 && !donefld)
        {
            await FldbldAsync();
        }
        else if ((vp.Tval & Rec) != 0 && !donerec)
        {
            await RecbldAsync();
        }

        if ((vp.Tval & Str) == 0)
        {
            vp.Sval = Cstr(Modf(vp.Fval, out _) == 0 ? Sprint("%.30g", A(vp.Fval)) : Lat(Libc.Sprint(Bytes(convfmtCell.Sval), [A(vp.Fval)])));
            vp.Tval = (vp.Tval & ~Dontfree) | Str;
        }

        return vp.Sval;
    }

    // qstring: s with its escapes undone, up to delim.
    private async Task<string> QstringAsync(string s, char delim)
    {
        var buf = new StringBuilder();
        string os = s;
        s += "\0";
        for (int i = 0; s[i] != delim; i++)
        {
            char c = s[i];
            if (c == '\n')
            {
                await SyntaxAsync($"newline in string {Lat(Libc.Sprint("%.20s"u8.ToArray(), [A(os)]))}...");
            }
            else if (c != '\\')
            {
                buf.Append(c);
            }
            else
            {
                c = s[++i];
                if (c == 0)
                {
                    buf.Append('\\');
                    break;
                }

                switch (c)
                {
                    case '\\':
                        buf.Append('\\');
                        break;
                    case 'n':
                        buf.Append('\n');
                        break;
                    case 't':
                        buf.Append('\t');
                        break;
                    case 'b':
                        buf.Append('\b');
                        break;
                    case 'f':
                        buf.Append('\f');
                        break;
                    case 'r':
                        buf.Append('\r');
                        break;
                    default:
                        if (!Isdigit(c))
                        {
                            buf.Append(c);
                            break;
                        }

                        int n = c - '0';
                        if (Isdigit(s[i + 1]))
                        {
                            n = (8 * n) + s[++i] - '0';
                            if (Isdigit(s[i + 1]))
                            {
                                n = (8 * n) + s[++i] - '0';
                            }
                        }

                        buf.Append((char)(byte)n);
                        break;
                }
            }
        }

        return Cstr(buf.ToString());
    }

    // lib.c.
    private void Recinit()
    {
        fldtab.Add(new Cell { Ctype = Ocell, Csub = Cfld, Tval = Rec | Str | Dontfree, Nval = "0" });
        Makefields(1, nfields);
    }

    private void Makefields(int n1, int n2)
    {
        for (int i = n1; i <= n2; i++)
        {
            fldtab.Add(new Cell { Ctype = Ocell, Csub = Cfld, Tval = Fld | Str | Dontfree, Nval = i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    private string Getargv(int n)
    {
        if (argvloc.Arr is not { } ap || Lookup(n.ToString(System.Globalization.CultureInfo.InvariantCulture), ap) is not { } x)
        {
            return string.Empty;
        }

        return GetsvalAsync(x).GetAwaiter().GetResult();
    }

    private async Task InitgetrecAsync()
    {
        for (int i = 1; i < argcloc.Fval; i++)
        {
            string p = Getargv(i);
            if (!Isclvar(p))
            {
                await SetsvalAsync(Lookup("FILENAME", symtab)!, p);
                return;
            }

            await SetclvarAsync(p);
            argno++;
        }

        infile = stdin;
    }

    // getrec: the next record, of the files of ARGV in turn, or standard input; into $0 or, for
    // getline var, given back.
    private async Task<(int N, string Buf)> GetrecAsync(bool isrecord)
    {
        if (firsttime)
        {
            firsttime = false;
            await InitgetrecAsync();
        }

        if (isrecord)
        {
            (donefld, donerec) = (false, true);
        }

        while (argno < argcloc.Fval || infile == stdin)
        {
            if (infile is null)
            {
                string file = Getargv(argno);
                if (file.Length == 0)
                {
                    argno++;
                    continue;
                }

                if (Isclvar(file))
                {
                    await SetclvarAsync(file);
                    argno++;
                    continue;
                }

                filenameCell.Sval = file;
                if (file == "-")
                {
                    infile = stdin;
                }
                else if ((infile = await BopenAsync(file, NinePConstants.OREAD)) is null)
                {
                    await FatalAsync($"can't open file {file}");
                }

                await SetfvalAsync(fnrloc, 0.0);
            }

            (int c, string buf) = await ReadrecAsync(infile!);
            if (c != 0 || buf.Length != 0)
            {
                if (isrecord)
                {
                    Cell f0 = fldtab[0];
                    (f0.Sval, f0.Tval) = (Cstr(buf), Rec | Str | Dontfree);
                    if (ToNumber(f0.Sval, out double f))
                    {
                        (f0.Fval, f0.Tval) = (f, f0.Tval | Num);
                    }
                }

                await SetfvalAsync(nrloc, nrloc.Fval + 1);
                await SetfvalAsync(fnrloc, fnrloc.Fval + 1);
                return (1, buf);
            }

            await NextfileAsync();
        }

        return (0, string.Empty);
    }

    private async Task NextfileAsync()
    {
        if (infile is not null && infile != stdin)
        {
            await infile.TermAsync();
        }

        infile = null;
        argno++;
    }

    // readrec: a record up to RS's first byte, or with RS empty up to a blank line, leading
    // newlines passed over.
    private async Task<(int C, string Buf)> ReadrecAsync(Bio inf)
    {
        if (Encoding.Latin1.GetByteCount(fsCell.Sval) >= 100)
        {
            await FatalAsync($"field separator {Lat(Libc.Sprint("%.10s"u8.ToArray(), [A(fsCell.Sval)]))}... is too long");
        }

        inputFS = fsCell.Sval;
        int sep = rsCell.Sval.Length > 0 ? rsCell.Sval[0] : 0, c;
        if (sep == 0)
        {
            sep = '\n';
            while ((c = await inf.B.BgetcAsync()) == '\n')
            {
            }

            if (c != Biobuf.Beof)
            {
                inf.B.Bungetc();
            }
        }

        var rr = new StringBuilder();
        for (; ;)
        {
            while ((c = await inf.B.BgetcAsync()) != sep && c != Biobuf.Beof)
            {
                rr.Append((char)c);
            }

            if ((rsCell.Sval.Length > 0 ? rsCell.Sval[0] : 0) == sep || c == Biobuf.Beof)
            {
                break;
            }

            if ((c = await inf.B.BgetcAsync()) is '\n' or Biobuf.Beof)
            {
                break;
            }

            rr.Append('\n').Append((char)c);
        }

        return (c == Biobuf.Beof && rr.Length == 0 ? 0 : 1, rr.ToString());
    }

    private async Task SetclvarAsync(string s)
    {
        int eq = s.IndexOf('=', StringComparison.Ordinal);
        string p = await QstringAsync(s[(eq + 1)..], '\0');
        Cell q = Setsymtab(s[..eq], p, 0.0, Str, symtab);
        await SetsvalAsync(q, p);
        if (ToNumber(q.Sval, out double f))
        {
            (q.Fval, q.Tval) = (f, q.Tval | Num);
        }
    }

    // fldbld: $1 to $NF of $0, split by FS as it was when the record was read.
    private async Task FldbldAsync()
    {
        if (donefld)
        {
            return;
        }

        if ((fldtab[0].Tval & Str) == 0)
        {
            await GetsvalAsync(fldtab[0]);
        }

        string r = fldtab[0].Sval;
        int i = 0, p = 0;
        if (inputFS.Length > 1)
        {
            i = await RefldbldAsync(r, inputFS);
        }
        else if (inputFS == " ")
        {
            for (; ;)
            {
                while (p < r.Length && r[p] is ' ' or '\t' or '\n')
                {
                    p++;
                }

                if (p >= r.Length)
                {
                    break;
                }

                int s = p;
                do
                {
                    p++;
                }
                while (p < r.Length && r[p] is not (' ' or '\t' or '\n'));
                SetField(++i, r[s..p], Fld | Str | Dontfree);
            }
        }
        else if (inputFS.Length == 0)
        {
            byte[] rb = Bytes(r);
            byte[] buf = new byte[Utf.UtfMax];
            for (int w; p < rb.Length; p += w)
            {
                w = Utf.CharToRune(out uint chr, rb.AsSpan(p));
                SetField(++i, Lat(buf.AsSpan(0, Utf.RuneToChar(buf, chr))), Fld | Str);
            }
        }
        else if (r.Length != 0)
        {
            char sep = inputFS[0];
            for (; ;)
            {
                int s = p;
                while (p < r.Length && r[p] != sep && r[p] != '\n')
                {
                    p++;
                }

                SetField(++i, r[s..p], Fld | Str | Dontfree);
                if (p++ >= r.Length)
                {
                    break;
                }
            }
        }

        Cleanfld(i + 1, lastfld);
        lastfld = i;
        donefld = true;
        for (int j = 1; j <= lastfld; j++)
        {
            Cell c = fldtab[j];
            if (ToNumber(c.Sval, out double f))
            {
                (c.Fval, c.Tval) = (f, c.Tval | Num);
            }
        }

        await SetfvalAsync(nfloc, lastfld);
    }

    private void SetField(int i, string s, int tval)
    {
        if (i > nfields)
        {
            Growfldtab(i);
        }

        (fldtab[i].Sval, fldtab[i].Tval) = (s, tval);
    }

    private void Cleanfld(int n1, int n2)
    {
        for (int i = n1; i <= n2; i++)
        {
            (fldtab[i].Sval, fldtab[i].Tval) = (string.Empty, Fld | Str | Dontfree);
        }
    }

    private void Newfld(int n)
    {
        if (n > nfields)
        {
            Growfldtab(n);
        }

        Cleanfld(lastfld + 1, n);
        lastfld = n;
        nfloc.Fval = n;
        nfloc.Tval = (nfloc.Tval & ~Str) | Num;
    }

    private async Task<Cell> FieldadrAsync(int n)
    {
        if (n < 0)
        {
            await FatalAsync($"trying to access field {n}");
        }

        if (n > nfields)
        {
            Growfldtab(n);
        }

        return fldtab[n];
    }

    private void Growfldtab(int n)
    {
        int nf = Math.Max(2 * nfields, n);
        Makefields(nfields + 1, nf);
        nfields = nf;
    }

    // refldbld: the fields between the non-empty matches of FS as an expression.
    private async Task<int> RefldbldAsync(string rec, string fs)
    {
        if (rec.Length == 0)
        {
            return 0;
        }

        Regexp p = await CompreAsync(fs);
        int i, at = 0;
        for (i = 1; ; i++)
        {
            if (i > nfields)
            {
                Growfldtab(i);
            }

            if (Nematch(p, rec, at))
            {
                (fldtab[i].Sval, fldtab[i].Tval) = (rec[at..patbeg], Fld | Str | Dontfree);
                at = patbeg + patlen;
            }
            else
            {
                (fldtab[i].Sval, fldtab[i].Tval) = (rec[at..], Fld | Str | Dontfree);
                break;
            }
        }

        return i;
    }

    // recbld: $0 of $1 to $NF and OFS between them.
    private async Task RecbldAsync()
    {
        if (donerec)
        {
            return;
        }

        var r = new StringBuilder();
        for (int i = 1; i <= nfloc.Fval; i++)
        {
            r.Append(await GetsvalAsync(fldtab[i]));
            if (i < nfloc.Fval)
            {
                r.Append(ofsCell.Sval);
            }
        }

        (fldtab[0].Sval, fldtab[0].Tval) = (r.ToString(), Rec | Str | Dontfree);
        donerec = true;
    }

    // A variable or constant: its name, its value as a string and as a number, which of them is
    // good, and, for an array, its table, and for a function, its body.
    private sealed class Cell
    {
        public int Ctype { get; set; }

        public int Csub { get; set; }

        public int Tval { get; set; }

        public string? Nval { get; set; }

        public string Sval { get; set; } = string.Empty;

        public double Fval { get; set; }

        public Cell? Cnext { get; set; }

        public AwkArray? Arr { get; set; }

        public Node? Body { get; set; }

        public Cell Copy() => (Cell)MemberwiseClone();
    }

    // A symbol table: chains of cells hashed by name, rehashed four times larger as it fills.
    private sealed class AwkArray(int n)
    {
        public int Nelemt { get; set; }

        public int Size { get; set; } = n;

        public Cell?[] Tab { get; set; } = new Cell?[n];
    }
}
