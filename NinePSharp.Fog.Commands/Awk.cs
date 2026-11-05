using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// awk(1), as 9front's awk: its lexer, yacc's parser of awkgram.y run on the tables yacc made, the
// tree that builds, and the run of it, with tran.c's symbol tables and lib.c's records and fields.
// Its strings are C strings of bytes, held one byte to a char, which end at a NUL. Its note handler
// and its -d debugging output are left out.
public sealed partial class Awk
{
    private const int Recsize = 8 * 1024;
    private const int Nsymtab = 50;
    private const int Maxfld = 200;
    private const int FopenMax = 40;
    private const int Fulltab = 2;
    private const int Growtab = 4;

    // Cell.Tval.
    private const int Num = 0x1;
    private const int Str = 0x2;
    private const int Dontfree = 0x4;
    private const int Con = 0x8;
    private const int Arr = 0x10;
    private const int Fcn = 0x20;
    private const int Fld = 0x40;
    private const int Rec = 0x80;

    // The functions of BLTIN.
    private const int Flength = 1;
    private const int Fsqrt = 2;
    private const int Fexp = 3;
    private const int Flog = 4;
    private const int Fint = 5;
    private const int Fsystem = 6;
    private const int Frand = 7;
    private const int Fsrand = 8;
    private const int Fsin = 9;
    private const int Fcos = 10;
    private const int Fatan = 11;
    private const int Ftoupper = 12;
    private const int Ftolower = 13;
    private const int Fflush = 14;
    private const int Futf = 15;

    // Cell.Ctype.
    private const int Ocell = 1;
    private const int Obool = 2;
    private const int Ojump = 3;

    // Cell.Csub.
    private const int Ccopy = 6;
    private const int Ccon = 5;
    private const int Ctemp = 4;
    private const int Cvar = 2;
    private const int Cfld = 1;
    private const int Cunk = 0;
    private const int Btrue = 11;
    private const int Bfalse = 12;
    private const int Jexit = 21;
    private const int Jnext = 22;
    private const int Jbreak = 23;
    private const int Jcont = 24;
    private const int Jret = 25;
    private const int Jnextfile = 26;

    // Node.Ntype.
    private const int Nvalue = 1;
    private const int Nstat = 2;
    private const int Nexpr = 3;

    private readonly Process process;
    private readonly List<Bio?> wbufs = [.. new Bio?[20]];
    private readonly Bio stdin;
    private readonly Bio stdout;
    private readonly Bio stderr;

    // Stryker disable once String : RunMainAsync names it argv[0] before anything is said
    private string cmdname = "awk";
    private int compileTime = 2;
    private bool safe;
    private string? exitstatus;

    private Awk(Process process)
    {
        this.process = process;
        stdin = new Bio(this, 0, NinePConstants.OREAD, false);
        stdout = new Bio(this, 1, NinePConstants.OWRITE, false);
        stderr = new Bio(this, 2, NinePConstants.OWRITE, false);
    }

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var awk = new Awk(process);
        await awk.RunMainAsync([.. argv.Select(a => Lat(Encoding.UTF8.GetBytes(a)))]);
    }

    // A string of bytes, one to a char.
    private static string Lat(ReadOnlySpan<byte> b) => Encoding.Latin1.GetString(b);

    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    // A C string: as far as its first NUL.
    private static string Cstr(string s)
    {
        int nul = s.IndexOf('\0', StringComparison.Ordinal);
        return nul < 0 ? s : s[..nul];
    }

    private static bool Isdigit(int c) => c is >= '0' and <= '9';

    private static bool Isalpha(int c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static bool Isalnum(int c) => Isalpha(c) || Isdigit(c);

    private static bool Isxdigit(int c) => Isdigit(c) || c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    // The conversions of 6c's code for a double to a 32-bit and a 64-bit integer: one out of range
    // is the indefinite integer, the least, which C#'s cast saturates to below the range.
    private static int Ci(double d) => double.IsNaN(d) || d >= 2147483648.0 ? int.MinValue : (int)d;

    private static long Cv(double d) => double.IsNaN(d) || d >= 9223372036854775808.0 ? long.MinValue : (long)d;

    // modf's integer part.
    private static double Modf(double x, out double ip)
    {
        ip = Math.Truncate(x);
        return double.IsInfinity(x) ? 0 : x - ip;
    }

    // utfnlen: the runes of the first n bytes, a rune cut short at their end not counted.
    private static int Utfnlen(string s, int n) => Utfnlen(Bytes(s), n);

    private static int Utfnlen(byte[] s, int m)
    {
        int n = 0;
        for (int p = 0; p < m; n++)
        {
            // Stryker disable once Equality : a byte of RuneSelf is a full rune that CharToRune steps over by one too
            if (s[p] < Utf.RuneSelf)
            {
                p++;
                continue;
            }

            if (!Utf.FullRune(s.AsSpan(p, m - p)))
            {
                break;
            }

            p += Utf.CharToRune(out _, s.AsSpan(p, m - p));
        }

        return n;
    }

    private static string Sprint(string format, params Libc.FmtArg[] args) => Lat(Libc.Sprint(Bytes(format), args));

    private static Libc.FmtArg A(double d) => Libc.FmtArg.Of(d);

    private static Libc.FmtArg A(long v) => Libc.FmtArg.Of(v);

    private static Libc.FmtArg A(string s) => Libc.FmtArg.Of(Bytes(s));

    private async Task RunMainAsync(List<string> argv)
    {
        cmdname = argv[0];
        if (argv.Count == 1)
        {
            await stderr.PrintAsync($"usage: {cmdname} [-F fieldsep] [-d] [-mf n] [-mr n] [-safe] [-v var=value] [-f programfile | 'program'] [file ...]\n");
            await ExitsAsync("usage");
        }

        // Stryker disable once Statement : lrand seeds with 1 when it is first called unseeded, and its sequence is the same
        Srand(1);
        symtab = Makesymtab(Nsymtab);
        string? fs = null;
        int i = 1;
        for (; i < argv.Count && argv[i].Length > 1 && argv[i][0] == '-'; i++)
        {
            string a = argv[i];
            if (a == "--")
            {
                i++;
                break;
            }

            switch (a[1])
            {
                case 's':
                    safe |= a == "-safe";
                    break;
                case 'f':
                    if (++i >= argv.Count)
                    {
                        await FatalAsync("no program filename");
                    }

                    pfile.Add(argv[i]);
                    break;
                case 'F':
                    if (a.Length > 2)
                    {
                        fs = a == "-Ft" ? "\t" : a[2..];
                    }
                    else if (++i < argv.Count)
                    {
                        fs = argv[i] == "t" ? "\t" : argv[i].Length > 0 ? argv[i] : fs;
                    }

                    if (fs is null)
                    {
                        await WarningAsync("field separator FS is empty");
                    }

                    break;
                case 'v':
                    if (a.Length == 2 && ++i < argv.Count && Isclvar(argv[i]))
                    {
                        await SetclvarAsync(argv[i]);
                    }

                    break;
                case 'd':
                    dbg = Libc.Strtol(a[2..], 10, out _);

                    // Stryker disable once Conditional : dbg is only asked whether it is 0
                    dbg = dbg == 0 ? 1 : dbg;
                    break;
                default:
                    await WarningAsync($"unknown option {a} ignored");
                    break;
            }
        }

        if (pfile.Count == 0)
        {
            if (i >= argv.Count)
            {
                if (dbg != 0)
                {
                    await ExitsAsync(null);
                }

                await FatalAsync("no program given");
            }

            lexprog = argv[i++];
        }

        Recinit();
        Syminit();
        compileTime = 1;
        Arginit([cmdname, .. argv.Skip(i)]);
        await YyparseAsync();
        if (fs is not null)
        {
            fsCell.Sval = await QstringAsync(fs, '\0');
        }

        if (exitstatus is null)
        {
            compileTime = 0;
            await RunAsync(winner);
        }
        else
        {
            await BracecheckAsync();
        }

        await ExitsAsync(exitstatus);
    }

    // exits, after libbio's atexit flushes the buffers being written in the order they were made.
    private async Task ExitsAsync(string? status)
    {
        for (int i = 0; i < wbufs.Count; i++)
        {
            if (wbufs[i] is { } bp)
            {
                wbufs[i] = null;
                await bp.FlushAsync();
            }
        }

        process.Exits(status is null ? null : Encoding.UTF8.GetString(Bytes(status)));
    }

    // SYNTAX: the message, where in the program, and its context; the program is not run.
    private async Task SyntaxAsync(string msg)
    {
        if (syntaxBeenHere++ > 2)
        {
            return;
        }

        await stderr.PrintAsync($"{cmdname}: {msg}");
        await stderr.PrintAsync(compileTime == 1 && Cursource() is { } src ? $" at {src}:{lineno}" : $" at line {lineno}");
        if (curfname is not null)
        {
            await stderr.PrintAsync($" in function {curfname}");
        }

        await stderr.PrintAsync("\n");
        exitstatus = "syntax error";
        await EprintAsync();
    }

    private async Task FatalAsync(string msg)
    {
        await stdout.FlushAsync();
        await stderr.PrintAsync($"{cmdname}: {msg}");
        await ErrorAsync();
        await ExitsAsync("FATAL");
    }

    private async Task WarningAsync(string msg)
    {
        await stdout.FlushAsync();
        await stderr.PrintAsync($"{cmdname}: {msg}");
        await ErrorAsync();
    }

    // error: the input record and the program's line where it was, and its context.
    private async Task ErrorAsync()
    {
        await stderr.PrintAsync("\n");
        if (compileTime != 2 && nrloc is not null && nrloc.Fval > 0)
        {
            await stderr.PrintAsync(filenameCell.Sval != "-" ? $" input record {filenameCell.Sval}:{Ci(fnrloc.Fval)}" : $" input record number {Ci(fnrloc.Fval)}");
            await stderr.PrintAsync("\n");
        }

        int line = compileTime != 2 && curnode is not null ? curnode.Lineno : compileTime != 2 && lineno != 0 ? lineno : -1;
        if (compileTime == 1 && Cursource() is { } src)
        {
            // Stryker disable once Equality,String : compiling, line is lineno, which starts at 1
            await stderr.PrintAsync(line >= 0 ? $" source {src}:{line}" : $" source file {src}");
        }

        // Stryker disable once Equality : a line is lineno or a node's, both from 1
        else if (line >= 0)
        {
            await stderr.PrintAsync($" source line {line}");
        }

        await stderr.PrintAsync("\n");
        await EprintAsync();
    }

    // eprint: the program about where the lexer is, the token it took marked.
    private async Task EprintAsync()
    {
        if (compileTime is 2 or 0 || eprintBeenHere++ > 0)
        {
            return;
        }

        int p = ep - 1;

        // Stryker disable once Equality : a newline is never the first byte an error stops at, as the grammar takes it there
        if (p > 0 && Eb(p) == '\n')
        {
            p--;
        }

        // Stryker disable once Equality : before ebuf Eb gives 0, which the printing below skips as it skips a NUL
        for (; p > 0 && Eb(p) != '\n' && Eb(p) != 0; p--)
        {
        }

        while (Eb(p) == '\n')
        {
            p++;
        }

        await stderr.PrintAsync(" context is\n\t");
        int q = ep - 1;

        // Stryker disable once Equality : stopping at p or below it, nothing is printed before p either way
        for (; q >= p && Eb(q) is not (' ' or '\t' or '\n'); q--)
        {
        }

        for (; p < q; p++)
        {
            if (Eb(p) != 0)
            {
                await stderr.PutcAsync((byte)Eb(p));
            }
        }

        await stderr.PrintAsync(" >>> ");
        for (; p < ep; p++)
        {
            if (Eb(p) != 0)
            {
                await stderr.PutcAsync((byte)Eb(p));
            }
        }

        await stderr.PrintAsync(" <<< ");
        if (Eb(ep) != 0)
        {
            for (int c; (c = await InputAsync()) is not ('\n' or 0 or Biobuf.Beof);)
            {
                await stderr.PutcAsync((byte)c);
                Bclass(c);
            }
        }

        await stderr.PutcAsync((byte)'\n');
        ep = 0;
    }

    // ebuf's byte, where eprint reads before it or past it, before anything was read, a 0.
    private int Eb(int i) => i >= 0 && i < ebuf.Length ? ebuf[i] : 0;

    private void Bclass(int c)
    {
        switch (c)
        {
            case '{':
                bracecnt++;
                break;
            case '}':
                bracecnt--;
                break;
            case '[':
                brackcnt++;
                break;
            case ']':
                brackcnt--;
                break;
            case '(':
                parencnt++;
                break;
            case ')':
                parencnt--;
                break;
        }
    }

    // bracecheck: the rest of the program read for its brackets, and those unmatched told.
    private async Task BracecheckAsync()
    {
        // Stryker disable once Update : counted down, a second call finds -1, which is not 0 either
        if (bracecheckBeenHere++ != 0)
        {
            return;
        }

        for (int c; (c = await InputAsync()) is not (Biobuf.Beof or 0);)
        {
            Bclass(c);
        }

        await Bcheck2Async(bracecnt, '}');
        await Bcheck2Async(brackcnt, ']');
        await Bcheck2Async(parencnt, ')');
    }

    private async Task Bcheck2Async(int n, char c2)
    {
        // Stryker disable once Equality : 1 and -1 are taken by the arms before
        string? m = n switch
        {
            1 => $"\tmissing {c2}\n",
            > 1 => $"\t{n} missing {c2}'s\n",
            -1 => $"\textra {c2}\n",
            < -1 => $"\t{-n} extra {c2}'s\n",
            _ => null,
        };
        if (m is not null)
        {
            await stderr.PrintAsync(m);
        }
    }

    private async Task<double> ErrcheckAsync(double x, string s)
    {
        if (double.IsNaN(x))
        {
            await WarningAsync($"{s} argument out of domain");
            x = 1;
        }
        else if (double.IsInfinity(x))
        {
            await WarningAsync($"{s} result out of range");
            x = 1;
        }

        return x;
    }

    // A Biobuf of awk's, which libbio's atexit flushes if it is written, and Bterm closes if it was
    // opened rather than set up on a descriptor of the program's.
    private sealed class Bio
    {
        private readonly Awk awk;
        private readonly bool opened;

        public Bio(Awk awk, int fd, int mode, bool opened)
        {
            (this.awk, Fd, this.opened) = (awk, fd, opened);
            B = new Biobuf(awk.process, fd);
            if (mode == NinePConstants.OWRITE)
            {
                int i = awk.wbufs.IndexOf(null);
                if (i >= 0)
                {
                    awk.wbufs[i] = this;
                }
            }
        }

        public int Fd { get; }

        public Biobuf B { get; }

        public Task<long> WriteAsync(string s) => B.BwriteAsync(Bytes(s));

        public async Task<int> PrintAsync(string s) => (int)await WriteAsync(s);

        public Task<int> PutcAsync(byte c) => B.BputcAsync(c);

        public Task<int> FlushAsync() => B.BflushAsync();

        public async Task<int> TermAsync()
        {
            int i = awk.wbufs.IndexOf(this);
            if (i >= 0)
            {
                awk.wbufs[i] = null;
            }

            int r = await B.BflushAsync();
            if (opened)
            {
                await awk.process.CloseAsync(Fd);
            }

            return r;
        }
    }
}
