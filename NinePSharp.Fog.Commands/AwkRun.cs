using System.Runtime.CompilerServices;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// awk's run.c, the walk of the tree, with re.c's patterns and popen.c's pipes.
public sealed partial class Awk
{
    private const int Nargs = 50;
    private const int Maxre = 512;
    private const int Maxnumsize = 50;
    private const int Maxforks = 20;

    private static readonly Cell True = new() { Ctype = Obool, Csub = Btrue, Tval = Num, Fval = 1.0 };
    private static readonly Cell False = new() { Ctype = Obool, Csub = Bfalse, Tval = Num };
    private static readonly Cell Jbreakc = new() { Ctype = Ojump, Csub = Jbreak, Tval = Num };
    private static readonly Cell Jcontc = new() { Ctype = Ojump, Csub = Jcont, Tval = Num };
    private static readonly Cell Jnextc = new() { Ctype = Ojump, Csub = Jnext, Tval = Num };
    private static readonly Cell Jnextfilec = new() { Ctype = Ojump, Csub = Jnextfile, Tval = Num };
    private static readonly Cell Jexitc = new() { Ctype = Ojump, Csub = Jexit, Tval = Num };
    private static readonly Cell Jretc = new() { Ctype = Ojump, Csub = Jret, Tval = Num };

    private readonly int[] pairstack = new int[50];
    private readonly Dictionary<string, Regexp> patterns = [];
    private readonly Files[] files = new Files[FopenMax];
    private readonly (int Pid, int Fd, bool Done, string Status)[] theFork = new (int, int, bool, string)[Maxforks];
    private readonly uint[] rngVec = new uint[607];
    private int rngTap;

    // Stryker disable once Unary : srand, which awk calls first, sets it
    private int rngFeed = -1;
    private double srandSeed = 1;
    private Node? curnode;
    private Frame? fp;
    private int patbeg;
    private int patlen;

    private static bool Isjump(Cell n) => n.Ctype == Ojump;

    private static bool Istrue(Cell n) => n.Csub == Btrue;

    private static Cell Gettemp() => new() { Ctype = Ocell, Csub = Ctemp, Tval = Num | Str | Dontfree };

    private static int Ptoi(object? p) => p is int i ? i : 0;

    // ipow: x**n, by halves; null where a product overflows, which 9front's floating point traps.
    private static double? Ipow(double x, int n)
    {
        if (n <= 0)
        {
            return 1;
        }

        if (Ipow(x, n / 2) is not { } v || Overflows(v * v, v, v) is not { } vv)
        {
            return null;
        }

        return n % 2 == 0 ? vv : Overflows(x * v, x, v) is { } xv ? Overflows(xv * v, xv, v) : null;
    }

    // A result that overflowed from finite operands, null, as amd64's trap on overflow, which
    // 9front's awk leaves enabled, stops it.
    private static double? Overflows(double r, double a, double b) => double.IsInfinity(r) && double.IsFinite(a) && double.IsFinite(b) ? null : r;

    // log, as 9front's libc has it: NaN for no more than 0.
    private static double Log(double x) => x <= 0 ? double.NaN : Math.Log(x);

    private async Task RunAsync(Node? a)
    {
        files[0] = new Files(stdin, "/dev/stdin", Lt);
        files[1] = new Files(stdout, "/dev/stdout", Gt);
        files[2] = new Files(stderr, "/dev/stderr", Gt);
        await ExecuteAsync(a);
        await CloseallAsync();
    }

    // A program that recurses as deep as its input takes it goes on where its thread's stack runs
    // short on another's, and faults past Maxdepth executes deep, as 9front's faults past its stack;
    // there is no stack left for awk's handler, so what it buffered is lost and only the kernel says
    // what it died of.
    private async Task<Cell> ExecuteAsync(Node? u)
    {
        if (++depth > Maxdepth)
        {
            await process.FaultAsync();
        }

        try
        {
            // Stryker disable once Boolean : a yield at each execute keeps the stack as short, only slower
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                await Task.Yield();
            }

            return await StatementsAsync(u);
        }
        finally
        {
            depth--;
        }
    }

    // execute: the node, and the statements after it, until a jump or an expression's value.
    private async Task<Cell> StatementsAsync(Node? u)
    {
        if (u is null)
        {
            return True;
        }

        for (Node a = u; ; a = a.Nnext!)
        {
            curnode = a;
            Cell x;
            if (a.Ntype == Nvalue)
            {
                x = (Cell)a.Narg[0]!;

                // Stryker disable once Statement : a value node holds a variable, a constant or $0 for a builtin, each of which is built when read
                await FieldsAsync(x);
                return x;
            }

            x = await ProcAsync(a.Narg, a.Nobj);
            await FieldsAsync(x);
            if (a.Ntype == Nexpr || Isjump(x) || a.Nnext is null)
            {
                return x;
            }
        }
    }

    private async Task FieldsAsync(Cell x)
    {
        if ((x.Tval & Fld) != 0 && !donefld)
        {
            await FldbldAsync();
        }
        else if ((x.Tval & Rec) != 0 && !donerec)
        {
            await RecbldAsync();
        }
    }

    // proctab.
    private Task<Cell> ProcAsync(object?[] a, int n) => n switch
    {
        Program => ProgramAsync(a),
        Pastat => PastatAsync(a),
        Pastat2 => Dopa2Async(a),
        Array => ArrayAsync(a),
        Match or Notmatch or Matchfcn => MatchopAsync(a, n),
        And or Bor or Not => BoolopAsync(a, n),
        Eq or Ge or Gt or Le or Lt or Ne => RelopAsync(a, n),
        In => InstatAsync(a),
        Arg => ArgAsync(a),
        Bltin => BltinAsync(a),
        Break or Continue or Exit or Next or Nextfile or Return => JumpAsync(a, n),
        Close => ClosefileAsync(a),
        Delete => AwkdeleteAsync(a),
        Do => DostatAsync(a),
        For => ForstatAsync(a),
        Sub => SubAsync(a),
        Gsub => GsubAsync(a),
        If => IfstatAsync(a),
        Index => SindexAsync(a),
        Add or Minus or Mult or Divide or Mod or Uminus or Power => ArithAsync(a, n),
        Assign or Addeq or Subeq or Multeq or Diveq or Modeq or Poweq => AssignAsync(a, n),
        Print => PrintstatAsync(a),
        Printf => AwkprintfAsync(a),
        Sprintf => AwksprintfAsync(a),
        Intest => IntestAsync(a),
        Condexpr => CondexprAsync(a),
        Postincr or Preincr or Postdecr or Predecr => IncrdecrAsync(a, n),
        Varnf => GetnfAsync(a),
        Call => CallAsync(a),
        Getline => GetlineAsync(a),
        Split => SplitAsync(a),
        Substr => SubstrAsync(a),
        While => WhilestatAsync(a),
        Cat => CatAsync(a),
        Indirect => IndirectAsync(a),
        _ => IllegalAsync(),
    };

    // The note of the overflow trap: the handler flushes standard output and prints it, and the
    // process dies of it, its other buffers unflushed. 9front's note gives the trap's addresses
    // too, which Fog has none of.
    private async Task<double> OverflowAsync()
    {
        const string Note = "sys: fp: numeric overflow";
        await stdout.FlushAsync();
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{Note}\n"));
        process.Exits(Note);
        return 0;
    }

    // Stryker disable once Block : the grammar makes no node proctab has no entry for
    private async Task<Cell> IllegalAsync()
    {
        // Stryker disable once Statement,String : the grammar makes no node proctab has no entry for
        await FatalAsync("illegal statement");
        return True;
    }

    private Task<Cell> X(object? n) => ExecuteAsync((Node?)n);

    // program: BEGIN, the records through the patterns, and END; exit leaves where it is.
    private async Task<Cell> ProgramAsync(object?[] a)
    {
        try
        {
            if (a[0] is not null)
            {
                Cell x = await X(a[0]);

                // Stryker disable once Block : exit leaves by its throw, not by its value
                if (x.Csub == Jexit)
                {
                    return True;
                }

                if (Isjump(x))
                {
                    await FatalAsync("illegal break, continue, next or nextfile from BEGIN");
                }
            }

            if (a[1] is not null || a[2] is not null)
            {
                while ((await GetrecAsync(true)).N > 0)
                {
                    // Stryker disable once Statement,Block : exit leaves by its throw, not by its value
                    if ((await X(a[1])).Csub == Jexit)
                    {
                        break;
                    }
                }
            }
        }
        catch (ExitJump)
        {
        }

        try
        {
            if (a[2] is not null)
            {
                Cell x = await X(a[2]);
                if (x.Csub is Jbreak or Jnext or Jcont)
                {
                    await FatalAsync("illegal break, continue, next or nextfile from END");
                }
            }
        }
        catch (ExitJump)
        {
        }

        return True;
    }

    // call: a function's body run with copies of the arguments, arrays by reference, and the
    // parameters past them empty locals.
    private async Task<Cell> CallAsync(object?[] a)
    {
        Cell fcn = await X(a[0]);
        string s = fcn.Nval!;
        if ((fcn.Tval & Fcn) == 0)
        {
            await FatalAsync($"calling undefined function {s}");
        }

        int ncall = 0;
        for (var x = (Node?)a[1]; x is not null; x = x.Nnext)
        {
            ncall++;
        }

        int ndef = Ci(fcn.Fval);
        if (ncall > ndef)
        {
            await WarningAsync($"function {s} called with {ncall} args, uses only {ndef}");
            ncall = ndef;
        }

        if (ndef + ncall > Nargs)
        {
            await FatalAsync($"function {s} has {ndef + ncall} arguments, limit {Nargs}");
        }

        var args = new Cell[ndef];
        var oargs = new Cell?[ndef];
        int i = 0;
        for (var x = (Node?)a[1]; i < ncall && x is not null; i++, x = x.Nnext)
        {
            Cell? y = await ExecuteAsync(x);
            if ((y.Tval & Fcn) != 0)
            {
                await FatalAsync($"can't use function {y.Nval} as argument in {s}");
            }

            if ((y.Tval & Arr) != 0)
            {
                args[i] = y;
                y = null;
            }
            else if (y.Csub == Ctemp)
            {
                y.Csub = Ccopy;
                args[i] = y;
            }
            else
            {
                args[i] = Copycell(y);
            }

            oargs[i] = y;
        }

        for (; i < ndef; i++)
        {
            args[i] = new Cell { Ctype = Ocell, Csub = Ccopy, Tval = Num | Str | Dontfree };
        }

        Frame? up = fp;
        fp = new Frame(fcn, args, ndef, Gettemp());
        Cell yr = await ExecuteAsync(fcn.Body);
        for (i = 0; i < ndef; i++)
        {
            Cell t = args[i];

            // Stryker disable once Equality : past ncall oargs holds nothing
            if ((t.Tval & Arr) != 0 && t.Csub == Ccopy && i < ncall && oargs[i] is { } o)
            {
                // Stryker disable once Bitwise : array() leaves an array's cell no flag but Arr
                o.Tval = t.Tval & ~(Str | Num | Dontfree);
                o.Arr = t.Arr;
            }
        }

        // Stryker disable once Logical,Block : exit leaves by its throw, and next and nextfile are refused in a function
        if (yr.Csub is Jexit or Jnext or Jnextfile)
        {
            return yr;
        }

        Cell z = fp.Retval;
        fp = up;
        return z;
    }

    private Cell Copycell(Cell x)
    {
        Cell y = Gettemp();
        (y.Csub, y.Nval, y.Sval, y.Fval) = (Ccopy, x.Nval, x.Sval, x.Fval);

        // Stryker disable once Bitwise : an argument of $0 is executed, so $0 is built, before a copy of it is set
        y.Tval = x.Tval & ~(Con | Fld | Rec | Dontfree);

        // Stryker disable once Assignment,Conditional,Equality : Dontfree is never read
        y.Tval |= y.Sval.Length == 0 ? Dontfree : 0;
        y.Arr = x.Arr;
        return y;
    }

    private async Task<Cell> ArgAsync(object?[] a)
    {
        int n = Ptoi(a[0]);

        // Stryker disable once Equality,Statement,String,Arithmetic,Block : an argument's number is one of its function's parameters
        if (n >= fp!.Nargs)
        {
            await FatalAsync($"argument #{n + 1} of function {fp.Fcncell.Nval} was not supplied");
        }

        return fp.Args[n];
    }

    // jump: break, continue, next, nextfile, return, and exit, which leaves for END or the end.
    private async Task<Cell> JumpAsync(object?[] a, int n)
    {
        switch (n)
        {
            case Exit:
                if (a[0] is not null)
                {
                    Cell y = await X(a[0]);
                    if ((y.Tval & (Num | Str)) == Str)
                    {
                        exitstatus = await GetsvalAsync(y);
                    }
                    else if (Ci(await GetfvalAsync(y)) != 0)
                    {
                        exitstatus = "error";
                    }
                }

                throw new ExitJump();
            case Return:
                if (a[0] is not null)
                {
                    Cell y = await X(a[0]);
                    if ((y.Tval & (Str | Num)) == (Str | Num))
                    {
                        await SetsvalAsync(fp!.Retval, await GetsvalAsync(y));
                        fp.Retval.Fval = await GetfvalAsync(y);

                        // Stryker disable once Assignment : setsval has cleared Num, so toggling it sets it
                        fp.Retval.Tval |= Num;
                    }
                    else if ((y.Tval & Str) != 0)
                    {
                        await SetsvalAsync(fp!.Retval, await GetsvalAsync(y));
                    }
                    else if ((y.Tval & Num) != 0)
                    {
                        await SetfvalAsync(fp!.Retval, await GetfvalAsync(y));
                    }
                    else
                    {
                        await FatalAsync($"bad type variable {y.Tval}");
                    }
                }

                return Jretc;
            case Next:
                return Jnextc;
            case Nextfile:
                await NextfileAsync();
                return Jnextfilec;
            case Break:
                return Jbreakc;
            default:
                return Jcontc;
        }
    }

    // getline: a record of the current input, a file, or a command, into $0 or a variable.
    private async Task<Cell> GetlineAsync(object?[] a)
    {
        await stdout.FlushAsync();
        Cell r = Gettemp();
        int n;
        if (a[1] is not null)
        {
            Cell x = await X(a[2]);
            int mode = Ptoi(a[1]);
            mode = mode == '|' ? Le : mode;
            Bio? f = await OpenfileAsync(mode, await GetsvalAsync(x));

            // Stryker disable once String : the record is read only when there was one
            string buf = string.Empty;
            if (f is null)
            {
                n = -1;
            }
            else
            {
                (n, buf) = await ReadrecAsync(f);
            }

            if (n > 0)
            {
                if (a[0] is not null)
                {
                    await SetsvalAsync(await X(a[0]), buf);
                }
                else
                {
                    await SetsvalAsync(fldtab[0], buf);
                    if (ToNumber(fldtab[0].Sval, out double f0))
                    {
                        (fldtab[0].Fval, fldtab[0].Tval) = (f0, fldtab[0].Tval | Num);
                    }
                }
            }
        }
        else if (a[0] is null)
        {
            n = (await GetrecAsync(true)).N;
        }
        else
        {
            (n, string buf) = await GetrecAsync(false);
            if (n > 0)
            {
                await SetsvalAsync(await X(a[0]), buf);
            }
        }

        await SetfvalAsync(r, n);
        return r;
    }

    private async Task<Cell> GetnfAsync(object?[] a)
    {
        if (!donefld)
        {
            await FldbldAsync();
        }

        return (Cell)a[0]!;
    }

    // The subscript of the list: its values joined by SUBSEP.
    private async Task<string> SubscriptAsync(Node? np)
    {
        var buf = new StringBuilder();
        for (; np is not null; np = np.Nnext)
        {
            buf.Append(await GetsvalAsync(await ExecuteAsync(np)));
            if (np.Nnext is not null)
            {
                buf.Append(subsepCell.Sval);
            }
        }

        return buf.ToString();
    }

    private void MakeArray(Cell x)
    {
        if ((x.Tval & Arr) == 0)
        {
            x.Tval = (x.Tval & ~(Str | Num | Dontfree)) | Arr;
            x.Arr = Makesymtab(Nsymtab);
        }
    }

    private async Task<Cell> ArrayAsync(object?[] a)
    {
        Cell x = await X(a[0]);
        string buf = await SubscriptAsync((Node?)a[1]);
        MakeArray(x);
        Cell z = Setsymtab(buf, string.Empty, 0.0, Str | Num, x.Arr!);
        (z.Ctype, z.Csub) = (Ocell, Cvar);
        return z;
    }

    private async Task<Cell> AwkdeleteAsync(object?[] a)
    {
        Cell x = await X(a[0]);
        if ((x.Tval & Arr) == 0)
        {
            return True;
        }

        if (a[1] is null)
        {
            // Stryker disable once Bitwise,Unary : an array's cell holds no flag but Arr
            x.Tval = (x.Tval & ~Str) | Arr;
            x.Arr = Makesymtab(Nsymtab);
        }
        else
        {
            Freeelem(x, await SubscriptAsync((Node?)a[1]));
        }

        return True;
    }

    private async Task<Cell> IntestAsync(object?[] a)
    {
        Cell ap = await X(a[1]);
        MakeArray(ap);
        string buf = await SubscriptAsync((Node?)a[0]);
        return Lookup(buf, ap.Arr!) is null ? False : True;
    }

    // matchop: ~ and !~, and match(), which sets RSTART and RLENGTH in runes.
    private async Task<Cell> MatchopAsync(object?[] a, int n)
    {
        Cell x = await X(a[1]);
        string s = await GetsvalAsync(x);
        Regexp p = a[0] is null ? (Regexp)a[2]! : await CompreAsync(await GetsvalAsync(await X(a[2])));

        // Stryker disable once Conditional : pmatch matches where match does
        bool i = n == Matchfcn ? Pmatch(p, s, 0) : Match1(p, s);
        if (n == Matchfcn)
        {
            int start = 0, len = patlen;
            if (patlen >= 0)
            {
                start = Utfnlen(s, patbeg) + 1;
                len = Utfnlen(s[patbeg..], patlen);
            }

            await SetfvalAsync(rstartloc, start);
            await SetfvalAsync(rlengthloc, len);
            x = Gettemp();
            (x.Tval, x.Fval) = (Num, start);
            return x;
        }

        return (n == Match && i) || (n == Notmatch && !i) ? True : False;
    }

    private async Task<Cell> BoolopAsync(object?[] a, int n)
    {
        bool i = Istrue(await X(a[0]));
        return n switch
        {
            Bor => i || Istrue(await X(a[1])) ? True : False,
            And => i && Istrue(await X(a[1])) ? True : False,
            _ => i ? False : True,
        };
    }

    private async Task<Cell> RelopAsync(object?[] a, int n)
    {
        Cell x = await X(a[0]);
        Cell y = await X(a[1]);
        int i;
        if ((x.Tval & Num) != 0 && (y.Tval & Num) != 0)
        {
            double j = x.Fval - y.Fval;
            i = j < 0 ? -1 : j > 0 ? 1 : 0;
        }
        else
        {
            i = Math.Sign(string.CompareOrdinal(await GetsvalAsync(x), await GetsvalAsync(y)));
        }

        bool r = n switch
        {
            Lt => i < 0,
            Le => i <= 0,
            Ne => i != 0,
            Eq => i == 0,
            Ge => i >= 0,
            _ => i > 0,
        };
        return r ? True : False;
    }

    // indirect: $(a[0]).
    private async Task<Cell> IndirectAsync(object?[] a)
    {
        Cell x = await X(a[0]);
        int m = Ci(await GetfvalAsync(x));
        if (m == 0)
        {
            string s = await GetsvalAsync(x);
            if (!ToNumber(s, out double f))
            {
                x.Fval = f;
                await FatalAsync($"illegal field $({s}), name \"{x.Nval}\"");
            }

            x.Fval = f;
        }

        x = await FieldadrAsync(m);
        (x.Ctype, x.Csub) = (Ocell, Cfld);
        return x;
    }

    // substr: m runes from the n'th, both kept within the string.
    private async Task<Cell> SubstrAsync(object?[] a)
    {
        Cell x = await X(a[0]);
        Cell y = await X(a[1]);
        Cell? z = a[2] is not null ? await X(a[2]) : null;
        byte[] s = Bytes(await GetsvalAsync(x));
        int k = Utfnlen(s, s.Length) + 1;

        // Stryker disable once Equality : of an empty string the bounds below give an empty string too
        if (k <= 1)
        {
            x = Gettemp();
            await SetsvalAsync(x, string.Empty);
            return x;
        }

        int m = Ci(await GetfvalAsync(y));

        // Stryker disable once Conditional,Equality : past the last rune the walk below stops at the string's end
        m = m <= 0 ? 1 : m > k ? k : m;

        // Stryker disable once Arithmetic : n is cut to k - m below
        int n = z is not null ? Ci(await GetfvalAsync(z)) : k - 1;

        // Stryker disable once Equality,Conditional,Arithmetic : the walk below stops at the string's end
        n = n < 0 ? 0 : n > k - m ? k - m : n;
        y = Gettemp();
        int p0 = 0;

        // Stryker disable once Equality : m runs out at the string's end, as Utfnlen counts the runes CharToRune steps over
        while (p0 < s.Length && --m != 0)
        {
            p0 += Utf.CharToRune(out _, s.AsSpan(p0));
        }

        int p = p0;

        // Stryker disable once Equality : n runs out at the string's end, as Utfnlen counts the runes CharToRune steps over
        for (; p < s.Length && n-- != 0; p += Utf.CharToRune(out _, s.AsSpan(p)))
        {
        }

        await SetsvalAsync(y, Lat(s.AsSpan(p0, Math.Min(p, s.Length) - p0)));
        return y;
    }

    private async Task<Cell> SindexAsync(object?[] a)
    {
        string s1 = await GetsvalAsync(await X(a[0]));
        string s2 = await GetsvalAsync(await X(a[1]));
        Cell z = Gettemp();
        double v = 0;

        // Stryker disable once Conditional,Equality : the empty string is at 0 of any string
        int i = s2.Length == 0 && s1.Length > 0 ? 0 : s1.IndexOf(s2, StringComparison.Ordinal);
        if (i >= 0 && s1.Length > 0)
        {
            v = Utfnlen(s1, i) + 1;
        }

        await SetfvalAsync(z, v);
        return z;
    }

    // format: printf's format with each conversion given its argument as the verb takes it.
    private async Task<string> FormatAsync(string s, Node? a)
    {
        string os = s;
        var p = new StringBuilder();
        s += "\0";
        int i = 0;
        while (s[i] != 0)
        {
            if (s[i] != '%')
            {
                p.Append(s[i++]);
                continue;
            }

            if (s[i + 1] == '%')
            {
                p.Append('%');
                i += 2;
                continue;
            }

            var fmt = new StringBuilder();
            for (; ; i++)
            {
                fmt.Append(s[i]);
                if (s[i] == 0)
                {
                    break;
                }

                if (Isalpha(s[i]) && s[i] is not ('l' or 'h' or 'L'))
                {
                    break;
                }

                if (s[i] == '*')
                {
                    Cell x = await ExecuteAsync(a);
                    a = a!.Nnext;
                    fmt.Length--;
                    fmt.Append(Ci(await GetfvalAsync(x)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            string f = fmt.ToString().TrimEnd('\0');
            int flag;
            char last = s[i];
            switch (last)
            {
                case 'f' or 'e' or 'g' or 'E' or 'G':
                    flag = 1;
                    break;
                case 'd' or 'i':
                    flag = 2;

                    // Stryker disable once Conditional,Arithmetic : Sprint gives an int the same by %d, %ld and %lld
                    f = s[i - 1] == 'l' ? f : f[..^1] + "ld";
                    break;
                case 'u':

                    // Stryker disable once Conditional,Equality,Arithmetic : flags 2 and 3 are converted alike
                    flag = s[i - 1] == 'l' ? 2 : 3;
                    f = f[..^1] + "ud";
                    break;
                case 'o' or 'x' or 'X':

                    // Stryker disable once Conditional,Equality,Arithmetic : flags 2 and 3 are converted alike
                    flag = s[i - 1] == 'l' ? 2 : 3;
                    f = f[..^1] + "u" + last;
                    break;
                case 's' or 'q':
                    flag = 4;
                    break;
                case 'c':

                    // Stryker disable once String : %c is made below without the format
                    f = f[..^1] + "C";
                    flag = 5;
                    break;
                default:
                    await WarningAsync($"weird printf conversion {f}");
                    flag = 0;
                    break;
            }

            if (a is null)
            {
                await FatalAsync($"not enough args in printf({os})");
            }

            Cell y = await ExecuteAsync(a);
            a = a!.Nnext;
            switch (flag)
            {
                case 0:
                    p.Append(f).Append(await GetsvalAsync(y));
                    break;
                case 1:
                    p.Append(Lat(Libc.Sprint(Bytes(f), [A(await GetfvalAsync(y))])));
                    break;
                case 2 or 3:
                    p.Append(Lat(Libc.Sprint(Bytes(f), [A(Ci(await GetfvalAsync(y)))])));
                    break;
                case 4:
                    p.Append(Cstr(Lat(Libc.Sprint(Bytes(f), [A(await GetsvalAsync(y))], quotefmt: true))));
                    break;
                default:
                    byte[] buf = new byte[Utf.UtfMax];
                    if ((y.Tval & Num) != 0)
                    {
                        uint r = unchecked((uint)Cv(await GetfvalAsync(y)));
                        p.Append(Lat(buf.AsSpan(0, Utf.RuneToChar(buf, r))));
                    }
                    else if (await GetsvalAsync(y) is { Length: > 0 } t)
                    {
                        Utf.CharToRune(out uint r, Bytes(t));
                        p.Append(Lat(buf.AsSpan(0, Utf.RuneToChar(buf, r))));
                    }

                    break;
            }

            if (s[i] != 0)
            {
                i++;
            }
        }

        for (; a is not null; a = a.Nnext)
        {
            await ExecuteAsync(a);
        }

        return p.ToString();
    }

    private async Task<Cell> AwksprintfAsync(object?[] a)
    {
        var n = (Node)a[0]!;
        string buf = await FormatAsync(await GetsvalAsync(await ExecuteAsync(n)), n.Nnext);
        Cell x = Gettemp();
        (x.Sval, x.Tval) = (Cstr(buf), Str);
        return x;
    }

    private async Task<Cell> AwkprintfAsync(object?[] a)
    {
        var n = (Node)a[0]!;
        string buf = await FormatAsync(await GetsvalAsync(await ExecuteAsync(n)), n.Nnext);
        Bio f = a[1] is not null ? await RedirectAsync(Ptoi(a[1]), (Node)a[2]!) : stdout;
        if (await f.WriteAsync(buf) < 0)
        {
            await FatalAsync($"write error on {Filename(f)}");
        }

        if (f != stdout)
        {
            await f.FlushAsync();
        }

        return True;
    }

    private async Task<Cell> ArithAsync(object?[] a, int n)
    {
        double i = await GetfvalAsync(await X(a[0])), j = 0;
        if (n != Uminus)
        {
            j = await GetfvalAsync(await X(a[1]));
        }

        Cell z = Gettemp();
        double i0 = i;
        switch (n)
        {
            case Add:
                i += j;
                break;
            case Minus:
                i -= j;
                break;
            case Mult:
                i *= j;
                break;
            case Divide:
                if (j == 0)
                {
                    await FatalAsync("division by zero");
                }

                i /= j;
                break;
            case Mod:
                if (j == 0)
                {
                    await FatalAsync("division by zero in mod");
                }

                Modf(i / j, out double v);
                i -= j * v;
                break;
            case Uminus:
                // 6c negates by subtracting from 0, so neither 0 nor -0 is made -0.
                i = 0 - i;
                break;
            default:

                // Stryker disable once Equality : x^0 is 1 by pow as by ipow
                i = j >= 0 && Modf(j, out _) == 0.0 ? Ipow(i, Ci(j)) ?? await OverflowAsync() : await ErrcheckAsync(Math.Pow(i, j), "pow");
                break;
        }

        if (n is Add or Minus or Mult or Divide && Overflows(i, i0, j) is null)
        {
            await OverflowAsync();
        }

        await SetfvalAsync(z, i);
        return z;
    }

    private async Task<Cell> IncrdecrAsync(object?[] a, int n)
    {
        Cell x = await X(a[0]);
        double xf = await GetfvalAsync(x);
        int k = n is Preincr or Postincr ? 1 : -1;
        if (n is Preincr or Predecr)
        {
            await SetfvalAsync(x, xf + k);
            return x;
        }

        Cell z = Gettemp();
        await SetfvalAsync(z, xf);
        await SetfvalAsync(x, xf + k);
        return z;
    }

    private async Task<Cell> AssignAsync(object?[] a, int n)
    {
        Cell y = await X(a[1]);
        Cell x = await X(a[0]);
        if (n == Assign)
        {
            // Stryker disable once Bitwise,Block : a variable assigned itself is set to the value and flags it has
            if (x == y && (x.Tval & (Fld | Rec)) == 0)
            {
                return x;
            }

            if ((y.Tval & (Str | Num)) == (Str | Num))
            {
                double yf = await GetfvalAsync(y);
                await SetsvalAsync(x, await GetsvalAsync(y));
                x.Fval = yf;

                // Stryker disable once Assignment : setsval has cleared Num, so toggling it sets it
                x.Tval |= Num;
            }
            else if ((y.Tval & Str) != 0)
            {
                await SetsvalAsync(x, await GetsvalAsync(y));
            }

            // Stryker disable once Bitwise : what is left has a number, or is an array or a function, which funnyvar and getfval refuse alike
            else if ((y.Tval & Num) != 0)
            {
                await SetfvalAsync(x, await GetfvalAsync(y));
            }
            else
            {
                await FunnyvarAsync(y, "read value of");
            }

            return x;
        }

        double xf = await GetfvalAsync(x), yv = await GetfvalAsync(y), x0 = xf;
        switch (n)
        {
            case Addeq:
                xf += yv;
                break;
            case Subeq:
                xf -= yv;
                break;
            case Multeq:
                xf *= yv;
                break;
            case Diveq:
                if (yv == 0)
                {
                    await FatalAsync("division by zero in /=");
                }

                xf /= yv;
                break;
            case Modeq:
                if (yv == 0)
                {
                    await FatalAsync("division by zero in %=");
                }

                Modf(xf / yv, out double v);
                xf -= yv * v;
                break;
            default:

                // Stryker disable once Equality : x^0 is 1 by pow as by ipow
                xf = yv >= 0 && Modf(yv, out _) == 0.0 ? Ipow(xf, Ci(yv)) ?? await OverflowAsync() : await ErrcheckAsync(Math.Pow(xf, yv), "pow");
                break;
        }

        if (n is Addeq or Subeq or Multeq or Diveq && Overflows(xf, x0, yv) is null)
        {
            await OverflowAsync();
        }

        await SetfvalAsync(x, xf);
        return x;
    }

    private async Task<Cell> CatAsync(object?[] a)
    {
        Cell x = await X(a[0]);
        Cell y = await X(a[1]);
        string s = await GetsvalAsync(x) + await GetsvalAsync(y);
        Cell z = Gettemp();
        (z.Sval, z.Tval) = (s, Str);
        return z;
    }

    private async Task<Cell> PastatAsync(object?[] a)
    {
        // Stryker disable once Block : executing no pattern gives True
        if (a[0] is null)
        {
            return await X(a[1]);
        }

        Cell x = await X(a[0]);
        return Istrue(x) ? await X(a[1]) : x;
    }

    private async Task<Cell> Dopa2Async(object?[] a)
    {
        int pair = Ptoi(a[3]);
        if (pairstack[pair] == 0 && Istrue(await X(a[0])))
        {
            pairstack[pair] = 1;
        }

        if (pairstack[pair] == 1)
        {
            if (Istrue(await X(a[1])))
            {
                pairstack[pair] = 0;
            }

            return await X(a[2]);
        }

        return False;
    }

    // split: the string into the array by FS, a string, or an expression.
    private async Task<Cell> SplitAsync(object?[] a)
    {
        string s = await GetsvalAsync(await X(a[0]));
        int arg3type = Ptoi(a[3]);

        // Stryker disable once String : with an expression the separator is not read
        string fs = a[2] is null ? fsCell.Sval : arg3type == String ? await GetsvalAsync(await X(a[2])) : "(regexpr)";
        int sep = fs.Length > 0 ? fs[0] : 0;
        Cell ap = await X(a[1]);
        ap.Tval = (ap.Tval & ~Str) | Arr;
        AwkArray tab = ap.Arr = Makesymtab(Nsymtab);
        int n = 0;

        void Put(string t)
        {
            string num = (++n).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ToNumber(t, out double f))
            {
                Setsymtab(num, t, f, Str | Num, tab);
            }
            else
            {
                Setsymtab(num, t, 0.0, Str, tab);
            }
        }

        if ((s.Length != 0 && fs.Length > 1) || arg3type == Regexpr)
        {
            Regexp p = arg3type == Regexpr ? (Regexp)a[2]! : await CompreAsync(fs);
            int t = 0;
            bool done = false;
            while (Nematch(p, s, t))
            {
                Put(s[t..patbeg]);
                t = patbeg + patlen;

                // Stryker disable once Logical,Equality,Statement : at the end no match follows, and what is left, put as the last field, is empty too
                if (s[t - 1] == 0 || t >= s.Length)
                {
                    Setsymtab((++n).ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty, 0.0, Str, tab);
                    done = true;
                    break;
                }
            }

            if (!done)
            {
                Put(s[t..]);
            }
        }
        else if (sep == ' ')
        {
            for (int p = 0; ;)
            {
                while (p < s.Length && s[p] is ' ' or '\t' or '\n')
                {
                    p++;
                }

                if (p >= s.Length)
                {
                    break;
                }

                int t = p;
                do
                {
                    p++;
                }
                while (p < s.Length && s[p] is not (' ' or '\t' or '\n'));
                Put(s[t..p]);

                // Stryker disable once Conditional,Equality : past the end or at a blank, the next round passes over it
                p += p < s.Length ? 1 : 0;
            }
        }
        else if (sep == 0)
        {
            byte[] b = Bytes(s);
            for (int p = 0, nb; p < b.Length; p += nb)
            {
                nb = Utf.CharToRune(out _, b.AsSpan(p));

                // Stryker disable once Arithmetic : CharToRune takes no more bytes than there are
                Put(Lat(b.AsSpan(p, Math.Min(nb, b.Length - p))));
            }
        }
        else if (s.Length != 0)
        {
            for (int p = 0; ;)
            {
                int t = p;
                while (p < s.Length && s[p] != sep && s[p] != '\n')
                {
                    p++;
                }

                Put(s[t..p]);
                if (p++ >= s.Length)
                {
                    break;
                }
            }
        }

        Cell x = Gettemp();
        (x.Tval, x.Fval) = (Num, n);
        return x;
    }

    private async Task<Cell> CondexprAsync(object?[] a) => Istrue(await X(a[0])) ? await X(a[1]) : await X(a[2]);

    private async Task<Cell> IfstatAsync(object?[] a)
    {
        Cell x = await X(a[0]);

        // Stryker disable once Conditional : an if's value is never a jump but from its statements
        return Istrue(x) ? await X(a[1]) : a[2] is not null ? await X(a[2]) : x;
    }

    private async Task<Cell> WhilestatAsync(object?[] a)
    {
        for (; ;)
        {
            Cell x = await X(a[0]);
            if (!Istrue(x))
            {
                return x;
            }

            x = await X(a[1]);
            if (x.Csub == Jbreak)
            {
                return True;
            }

            if (x.Csub is Jnext or Jexit or Jret)
            {
                return x;
            }
        }
    }

    private async Task<Cell> DostatAsync(object?[] a)
    {
        for (; ;)
        {
            Cell x = await X(a[0]);
            if (x.Csub == Jbreak)
            {
                return True;
            }

            if (x.Csub is Jnext or Jnextfile or Jexit or Jret)
            {
                return x;
            }

            x = await X(a[1]);
            if (!Istrue(x))
            {
                return x;
            }
        }
    }

    private async Task<Cell> ForstatAsync(object?[] a)
    {
        await X(a[0]);
        for (; ;)
        {
            if (a[1] is not null)
            {
                Cell c = await X(a[1]);
                if (!Istrue(c))
                {
                    return c;
                }
            }

            Cell x = await X(a[3]);
            if (x.Csub == Jbreak)
            {
                return True;
            }

            if (x.Csub is Jnext or Jexit or Jret)
            {
                return x;
            }

            await X(a[2]);
        }
    }

    // instat: for (a[0] in a[1]), through the table's chains in order.
    private async Task<Cell> InstatAsync(object?[] a)
    {
        Cell vp = await X(a[0]);
        Cell arrayp = await X(a[1]);
        if ((arrayp.Tval & Arr) == 0)
        {
            return True;
        }

        AwkArray tp = arrayp.Arr!;
        for (int i = 0; i < tp.Size; i++)
        {
            for (Cell? cp = tp.Tab[i], ncp; cp is not null; cp = ncp)
            {
                await SetsvalAsync(vp, cp.Nval!);
                ncp = cp.Cnext;
                Cell x = await X(a[2]);
                if (x.Csub == Jbreak)
                {
                    return True;
                }

                if (x.Csub is Jnext or Jexit or Jret)
                {
                    return x;
                }
            }
        }

        return True;
    }

    private async Task<Cell> BltinAsync(object?[] a)
    {
        int t = Ptoi(a[0]);
        var arg = (Node)a[1]!;
        Cell x = await ExecuteAsync(arg);
        Node? nextarg = arg.Nnext;
        double u;
        switch (t)
        {
            case Flength:
                u = (x.Tval & Arr) != 0 ? x.Arr!.Nelemt : Utfnlen(await GetsvalAsync(x), (await GetsvalAsync(x)).Length);
                break;
            case Flog:
                u = await ErrcheckAsync(Log(await GetfvalAsync(x)), "log");
                break;
            case Fint:
                Modf(await GetfvalAsync(x), out u);
                break;
            case Fexp:
                u = await ErrcheckAsync(Math.Exp(await GetfvalAsync(x)), "exp");
                break;
            case Fsqrt:
                u = await ErrcheckAsync(Math.Sqrt(await GetfvalAsync(x)), "sqrt");
                break;
            case Fsin:
                u = Math.Sin(await GetfvalAsync(x));
                break;
            case Fcos:
                u = Math.Cos(await GetfvalAsync(x));
                break;
            case Fatan:
                if (nextarg is null)
                {
                    await WarningAsync("atan2 requires two arguments; returning 1.0");
                    u = 1.0;
                }
                else
                {
                    u = Math.Atan2(await GetfvalAsync(x), await GetfvalAsync(await ExecuteAsync(nextarg)));
                    nextarg = nextarg.Nnext;
                }

                break;
            case Fsystem:
                await stdout.FlushAsync();
                u = await SystemAsync(await GetsvalAsync(x));
                break;
            case Frand:
                u = FrandValue();
                break;
            case Fsrand:
                double tmp = (x.Tval & Rec) != 0 ? Random.Shared.Next() : await GetfvalAsync(x);
                Srand(unchecked((int)(uint)Cv(tmp)));
                (u, srandSeed) = (srandSeed, tmp);
                break;
            case Ftoupper or Ftolower:
                byte[] b = Bytes(await GetsvalAsync(x));
                var rbuf = new List<byte>();
                byte[] rb = new byte[Utf.UtfMax];
                for (int s = 0; s < b.Length;)
                {
                    s += Utf.CharToRune(out uint wc, b.AsSpan(s));
                    if (wc <= Utf.RuneMax && !(wc is >= 0xD800 and <= 0xDFFF))
                    {
                        var rune = new Rune(wc);
                        wc = (uint)(t == Ftoupper ? Rune.ToUpperInvariant(rune) : Rune.ToLowerInvariant(rune)).Value;
                    }

                    rbuf.AddRange(rb.AsSpan(0, Utf.RuneToChar(rb, wc)).ToArray());
                }

                x = Gettemp();
                await SetsvalAsync(x, Lat([.. rbuf]));
                return x;
            case Fflush:
                if ((x.Tval & Rec) != 0 || (await GetsvalAsync(x)).Length == 0)
                {
                    await FlushAllAsync();
                    u = 0;
                }
                else
                {
                    u = await OpenfileAsync(Fflush, await GetsvalAsync(x)) is { } f ? await f.FlushAsync() : Biobuf.Beof;
                }

                break;
            default:
                byte[] mbc = new byte[Utf.UtfMax];
                uint wr = unchecked((uint)Ci(await GetfvalAsync(x)));
                x = Gettemp();
                await SetsvalAsync(x, Lat(mbc.AsSpan(0, Utf.RuneToChar(mbc, wr))));
                return x;
        }

        x = Gettemp();
        await SetfvalAsync(x, u);
        if (nextarg is not null)
        {
            await WarningAsync("warning: function has too many arguments");
            for (; nextarg is not null; nextarg = nextarg.Nnext)
            {
                await ExecuteAsync(nextarg);
            }
        }

        return x;
    }

    // printstat: each value and OFS after it, ORS after the last, and the output flushed.
    private async Task<Cell> PrintstatAsync(object?[] a)
    {
        Bio f = a[1] is null ? stdout : await RedirectAsync(Ptoi(a[1]), (Node)a[2]!);
        for (var x = (Node?)a[0]; x is not null; x = x.Nnext)
        {
            await f.WriteAsync(await GetsvalAsync(await ExecuteAsync(x)));
            if (await f.WriteAsync(x.Nnext is null ? orsCell.Sval : ofsCell.Sval) < 0)
            {
                await FatalAsync($"write error on {Filename(f)}");
            }
        }

        if (await f.FlushAsync() < 0)
        {
            await FatalAsync($"write error on {Filename(f)}");
        }

        return True;
    }

    private async Task<Bio> RedirectAsync(int a, Node b)
    {
        string fname = await GetsvalAsync(await ExecuteAsync(b));
        Bio? f = await OpenfileAsync(a, fname);
        if (f is null)
        {
            await FatalAsync($"can't open file {fname}");
        }

        return f!;
    }

    private bool Writing(int m) => m != Lt && m != Le;

    // openfile: the file of the name in this mode, opened if it is not open yet.
    private async Task<Bio?> OpenfileAsync(int a, string s)
    {
        if (s.Length == 0)
        {
            await FatalAsync("null file name in print or getline");
        }

        for (int i = 0; i < FopenMax; i++)
        {
            if (files[i] is { Fname: { } n } fi && n == s)
            {
                if (a == fi.Mode || (a == Append && fi.Mode == Gt))
                {
                    return fi.Fp;
                }

                if (a == Fflush)
                {
                    return Writing(fi.Mode) ? fi.Fp : null;
                }
            }
        }

        if (a == Fflush)
        {
            return null;
        }

        int slot = System.Array.FindIndex(files, f => f?.Fp is null);
        if (slot < 0)
        {
            await FatalAsync($"{s} makes too many open files");
        }

        await stdout.FlushAsync();
        int m = a;
        Bio? fp = null;
        if (a == Gt)
        {
            fp = await BopenAsync(s, NinePConstants.OWRITE);
        }
        else if (a == Append)
        {
            int fd = -1;
            try
            {
                fd = await process.OpenAsync(Path(s), NinePConstants.OWRITE);
            }
            catch (SyscallException)
            {
                try
                {
                    fd = await process.CreateAsync(Path(s), NinePConstants.OWRITE, 0b110_110_110);
                }
                catch (SyscallException)
                {
                }
            }

            if (fd >= 0)
            {
                await process.SeekAsync(fd, 0, 2);

                fp = new Bio(this, fd, NinePConstants.OWRITE, true);
                m = Gt;
            }
        }
        else if (a == '|')
        {
            fp = await PopenAsync(s, NinePConstants.OWRITE);
        }
        else if (a == Le)
        {
            fp = await PopenAsync(s, NinePConstants.OREAD);
        }
        else
        {
            fp = s == "-" ? stdin : await BopenAsync(s, NinePConstants.OREAD);
        }

        if (fp is not null)
        {
            files[slot] = new Files(fp, s, m);
        }

        return fp;
    }

    private string Path(string s) => Encoding.UTF8.GetString(Bytes(s));

    // Stryker disable once Linq,String : a file written to is in files
    private string Filename(Bio f) => files.FirstOrDefault(x => x?.Fp == f)?.Fname ?? "???";

    private async Task<Bio?> BopenAsync(string name, int mode)
    {
        try
        {
            int fd = mode == NinePConstants.OREAD
                ? await process.OpenAsync(Path(name), mode)
                : await process.CreateAsync(Path(name), mode, 0b110_110_110);

            return new Bio(this, fd, mode, true);
        }

        // Stryker disable once Block : what an empty catch would return
        catch (SyscallException)
        {
            return null;
        }
    }

    private async Task<Cell> ClosefileAsync(object?[] a)
    {
        string s = await GetsvalAsync(await X(a[0]));
        for (int i = 0; i < FopenMax; i++)
        {
            if (files[i] is { Fname: { } n } fi && n == s)
            {
                int stat = fi.Mode is '|' or Le ? await PcloseAsync(fi.Fp!) : await fi.Fp!.TermAsync();
                if (stat == Biobuf.Beof)
                {
                    await WarningAsync($"i/o error occurred closing {n}");
                }

                files[i] = new Files(null, null, 0);
            }
        }

        return True;
    }

    private async Task CloseallAsync()
    {
        for (int i = 0; i < FopenMax; i++)
        {
            if (files[i]?.Fp is { } f)
            {
                // Stryker disable once Conditional,Equality,Statement,String : pclose ends a file too and gives -1 as it is no pipe, and neither gives less than -1
                int stat = files[i]!.Mode is '|' or Le ? await PcloseAsync(f) : await f.TermAsync();

                // Stryker disable once Block : neither gives less than -1
                if (stat < -1)
                {
                    // Stryker disable once Statement,String : neither gives less than -1
                    await WarningAsync($"i/o error occurred while closing {files[i]!.Fname}");
                }
            }
        }
    }

    private async Task FlushAllAsync()
    {
        foreach (Files? f in files)
        {
            if (f?.Fp is { } fp && Writing(f.Mode))
            {
                await fp.FlushAsync();
            }
        }
    }

    // sub: the first match replaced, & the match and \& an &.
    private async Task<Cell> SubAsync(object?[] a)
    {
        Cell x = await X(a[3]);
        string t = await GetsvalAsync(x);
        Regexp p = a[0] is null ? (Regexp)a[1]! : await CompreAsync(await GetsvalAsync(await X(a[1])));
        Cell y = await X(a[2]);
        if (!Pmatch(p, t, 0))
        {
            return False;
        }

        var buf = new StringBuilder(t[..patbeg]);
        Replace(buf, await GetsvalAsync(y), t);
        int sptr = patbeg + patlen;
        buf.Append(t[sptr..]);
        await SetsvalAsync(x, buf.ToString());
        return True;
    }

    // The replacement with the match, at patbeg, put for &.
    private void Replace(StringBuilder pb, string r, string t)
    {
        r += "\0\0\0\0";
        for (int s = 0; r[s] != 0;)
        {
            if (r[s] == '\\')
            {
                if (r[s + 1] == '\\')
                {
                    if (r[s + 2] == '\\' && r[s + 3] == '&')
                    {
                        pb.Append("\\&");
                        s += 4;
                    }
                    else if (r[s + 2] == '&')
                    {
                        pb.Append('\\');
                        s += 2;
                    }
                    else
                    {
                        pb.Append(r[s]).Append(r[s + 1]);
                        s += 2;
                    }
                }
                else if (r[s + 1] == '&')
                {
                    pb.Append('&');
                    s += 2;
                }
                else
                {
                    pb.Append(r[s++]);
                }
            }
            else if (r[s] == '&')
            {
                s++;
                pb.Append(t, patbeg, patlen);
            }
            else
            {
                pb.Append(r[s++]);
            }
        }
    }

    // gsub: each match replaced, an empty one only where no match has just ended.
    private async Task<Cell> GsubAsync(object?[] a)
    {
        Cell x = await X(a[3]);
        string t = await GetsvalAsync(x);
        Regexp p = a[0] is null ? (Regexp)a[1]! : await CompreAsync(await GetsvalAsync(await X(a[1])));
        Cell y = await X(a[2]);
        int num = 0, c = 0;
        bool mflag = false;
        if (Pmatch(p, t, c))
        {
            var pb = new StringBuilder();
            string rptr = await GetsvalAsync(y);
            do
            {
                if (patlen == 0 && patbeg < t.Length)
                {
                    if (!mflag)
                    {
                        num++;
                        Replace(pb, rptr, t);
                    }

                    // Stryker disable once Equality,Statement,Block : an empty match before the end is at c or after it, so c is before the end
                    if (c >= t.Length)
                    {
                        break;
                    }

                    pb.Append(t[c++]);
                    mflag = false;
                }
                else
                {
                    num++;
                    pb.Append(t, c, patbeg - c);
                    Replace(pb, rptr, t);
                    c = patbeg + patlen;
                    if (c >= t.Length)
                    {
                        break;
                    }

                    mflag = true;
                }
            }
            while (Pmatch(p, t, c));
            pb.Append(t[Math.Min(c, t.Length)..]);
            await SetsvalAsync(x, pb.ToString());
        }

        x = Gettemp();
        (x.Tval, x.Fval) = (Num, num);
        return x;
    }

    // re.c: compre, the pattern made fit for regcomp, kept in the cache past compiling.
    private async Task<Regexp> CompreAsync(string pat)
    {
        // Stryker disable once Equality,Block : an expression compiled again is the same
        if (compileTime == 0 && patterns.TryGetValue(pat, out Regexp? cached))
        {
            return cached;
        }

        var re = new List<byte>();
        string s = pat + "\0\0";
        bool inclass = false;
        for (int i = 0; ;)
        {
            char c = s[i++];
            if (c == 0)
            {
                break;
            }

            if (c == '\\')
            {
                i = await QuotedAsync(s, i, re);
                continue;
            }

            if (!inclass && c == '(' && s[i] == ')')
            {
                re.AddRange("[]"u8.ToArray());
                c = '*';
                i++;
            }
            else if (c == '[')
            {
                inclass = true;
                if (s[i] == '-')
                {
                    re.AddRange("[\\"u8.ToArray());
                    c = s[i++];
                }
                else if (s[i] == '^' && s[i + 1] == '-')
                {
                    re.Add((byte)'[');
                    re.Add((byte)s[i++]);
                    re.Add((byte)'\\');
                    c = s[i++];
                }
                else if (s[i] == '[')
                {
                    re.Add((byte)c);
                    c = s[i++];
                }

                // Stryker disable once Logical,Arithmetic : [^ and a byte after it are copied as the branches below copy them, but for [^- and [^[
                else if (s[i] == '^' && s[i + 1] == '[')
                {
                    re.Add((byte)c);
                    re.Add((byte)s[i++]);
                    c = s[i++];
                }
                else if (s[i] == ']')
                {
                    re.Add((byte)c);
                    re.Add((byte)s[i++]);
                    c = '*';
                    inclass = false;
                }
            }
            else if (c == '-' && s[i] == ']')
            {
                re.Add((byte)'\\');
            }
            else if (c == ']')
            {
                inclass = false;
            }

            re.Add((byte)c);
            if (re.Count >= Maxre)
            {
                await FatalAsync("regular expression too big");
            }
        }

        Regexp program = null!;
        try
        {
            program = Regexp.Regcomp([.. re]);
        }
        catch (Regexp.RegerrorException e)
        {
            await FatalAsync(Lat(Encoding.UTF8.GetBytes(e.Message)));
        }

        // Stryker disable once Equality,Block : an expression compiled again is the same
        if (compileTime == 0)
        {
            patterns[pat] = program;
        }

        return program;
    }

    // quoted: an escape, its rune for \t \n \f \r \b, and the rune of \x and \ddd, or the byte,
    // after a backslash.
    private async Task<int> QuotedAsync(string s, int p, List<byte> t)
    {
        uint c = s[p++];
        switch (c)
        {
            case 't':
                c = '\t';
                break;
            case 'n':
                c = '\n';
                break;
            case 'f':
                c = '\f';
                break;
            case 'r':
                c = '\r';
                break;
            case 'b':
                c = '\b';
                break;
            default:
                t.Add((byte)'\\');
                if (c == 'x')
                {
                    int n = 0, i = 0;
                    for (; i < 4 && Isxdigit(s[p + i]); i++)
                    {
                        char h = s[p + i];
                        n = (16 * n) + (Isdigit(h) ? h - '0' : (h | 0x20) - 'a' + 10);
                    }

                    p += i;
                    c = (uint)n;
                }
                else if (c is >= '0' and <= '7')
                {
                    c -= '0';
                    if (s[p] is >= '0' and <= '7')
                    {
                        c = (8 * c) + s[p++] - '0';
                        if (s[p] is >= '0' and <= '7')
                        {
                            c = (8 * c) + s[p++] - '0';
                        }
                    }
                }
                else if (c >= 0x80)
                {
                    // A byte of a rune, as a negative char, is no rune.
                    c = unchecked((uint)(sbyte)(byte)c);
                }

                break;
        }

        byte[] buf = new byte[Utf.UtfMax];
        int w = Utf.RuneToChar(buf, c);
        if (t.Count >= Maxre - w)
        {
            await FatalAsync("regular expression too big");
        }

        t.AddRange(buf.AsSpan(0, w).ToArray());
        return p;
    }

    private bool Match1(Regexp p, string s) => p.Regexec(Bytes(s), null, 0) > 0;

    private bool Pmatch(Regexp p, string s, int start)
    {
        Regexp.Resub[] m = [new Regexp.Resub(start, -1)];
        if (p.Regexec(Bytes(s), m, 1) > 0)
        {
            (patbeg, patlen) = (m[0].Sp, m[0].Ep - m[0].Sp);
            return true;
        }

        (patbeg, patlen) = (start, -1);
        return false;
    }

    private bool Nematch(Regexp p, string s, int start)
    {
        if (Pmatch(p, s, start) && patlen > 0)
        {
            return true;
        }

        // Stryker disable once Unary : no caller reads patbeg or patlen after nematch fails
        (patbeg, patlen) = (start, -1);
        return false;
    }

    // popen: rc -c cmd, its standard input or output the other end of a pipe from this one.
    private async Task<Bio?> PopenAsync(string cmd, int mode)
    {
        int ind = System.Array.FindIndex(theFork, f => f.Pid == 0);
        if (ind < 0)
        {
            return null;
        }

        (int r, int w) = await process.PipeAsync();

        // Stryker disable once Conditional,Equality : a pipe carries bytes both ways
        (int myside, int hisside) = mode == NinePConstants.OREAD ? (r, w) : (w, r);
        long pid = process.Fork(RforkFlags.Fdg | RforkFlags.Rend | RforkFlags.Proc, async child =>
        {
            // Stryker disable once Statement : the loop below closes it, as a pipe's descriptors are past 2
            await child.CloseAsync(myside);
            await child.DupAsync(hisside, mode == NinePConstants.OREAD ? 1 : 0);

            // Stryker disable once Equality : nothing is open at FopenMax
            for (int i = 3; i < FopenMax; i++)
            {
                try
                {
                    await child.CloseAsync(i);
                }
                catch (SyscallException)
                {
                }
            }

            try
            {
                // Stryker disable once String : rc -c names no file by its argv[0]
                await child.ExecAsync("/bin/rc", ["rc", "-c", Path(cmd)]);
            }
            catch (SyscallException)
            {
            }

            // Stryker disable once Statement,String : /bin/rc execs, and exec does not come back
            child.Exits("exec failed");
        });

        // Stryker disable once String : pclose shows no status but by its 0 or 1, which close does not give
        theFork[ind] = ((int)pid, myside, false, string.Empty);
        await process.CloseAsync(hisside);
        return new Bio(this, myside, mode, true);
    }

    private async Task<int> PcloseAsync(Bio ptr)
    {
        int f = ptr.Fd;
        await ptr.TermAsync();
        int ind = System.Array.FindIndex(theFork, x => x.Fd == f && x.Pid != 0);
        if (ind < 0)
        {
            // Stryker disable once Unary : pclose is called only for a pipe popen opened
            return -1;
        }

        if (!theFork[ind].Done)
        {
            int r;
            do
            {
                Waitmsg? status = null;
                try
                {
                    status = await process.WaitAsync();
                    r = (int)status.Pid;
                }
                catch (SyscallException)
                {
                    r = -1;
                }

                // Stryker disable once Equality,Boolean,Statement : a status pclose records is shown only by its 0 or 1, which close does not give
                for (int j = 0; j < Maxforks; j++)
                {
                    if (r == theFork[j].Pid)
                    {
                        theFork[j] = theFork[j] with { Done = true, Status = status!.Message };
                        break;
                    }
                }
            }
            while (r != theFork[ind].Pid && r != -1);

            // Stryker disable once Equality,Unary,String : a status pclose records is shown only by its 0 or 1, which close does not give
            if (r == -1)
            {
                theFork[ind].Status = "No loved ones to wait for";
            }
        }

        theFork[ind].Pid = 0;

        // Stryker disable once Conditional,Equality : close gives True whatever pclose gives but -1
        return theFork[ind].Status.Length != 0 ? 1 : 0;
    }

    // system: rc -c s, waited for; 1 for a status, 0 for none.
    private async Task<int> SystemAsync(string s)
    {
        // Stryker disable once Bitwise : Fork always makes a process, and rc meets nothing at a rendezvous
        long pid = process.Fork(RforkFlags.Fdg | RforkFlags.Rend | RforkFlags.Proc, async child =>
        {
            try
            {
                // Stryker disable once String : rc -c names no file by its argv[0]
                await child.ExecAsync("/bin/rc", ["rc", "-c", Path(s)]);
            }
            catch (SyscallException)
            {
            }

            // Stryker disable once Statement,String : /bin/rc execs, and exec does not come back
            child.Exits("exec");
        });
        for (; ;)
        {
            Waitmsg status = await process.WaitAsync();
            if (status.Pid == pid)
            {
                return status.Message.Length != 0 ? 1 : 0;
            }
        }
    }

    // srand and lrand, as 9front's lrand.c: Mitchell and Reeds' additive generator.
    private void Srand(int seed)
    {
        const int a = 48271, m = 2147483647, q = 44488, r = 3399;
        rngTap = 0;
        rngFeed = 607 - 273;
        int x = seed % m;

        // Stryker disable once Conditional : a seed less than m is congruent to it, and Schrage's steps keep that
        x += x < 0 ? m : 0;
        x = x == 0 ? 89482311 : x;
        for (int i = -20; i < 607; i++)
        {
            int hi = x / q, lo = x % q;
            x = (a * lo) - (r * hi);

            // Stryker disable once Equality : a step of 48271 mod a prime gives no 0
            x += x < 0 ? m : 0;
            if (i >= 0)
            {
                rngVec[i] = (uint)x;
            }
        }
    }

    private uint Lrand()
    {
        if (--rngTap < 0)
        {
            // Stryker disable once Equality,Statement,Update,Block : awk calls srand first, and the feed is 334 whenever the tap wraps
            if (rngFeed < 0)
            {
                Srand(1);
                rngTap--;
            }

            rngTap += 607;
        }

        if (--rngFeed < 0)
        {
            rngFeed += 607;
        }

        uint x = (rngVec[rngFeed] + rngVec[rngTap]) & 0x7fffffff;
        return rngVec[rngFeed] = x;
    }

    private double FrandValue()
    {
        const double norm = 1.0 / (1.0 + 0x7fffffff);
        double x;
        do
        {
            x = Lrand() * norm;
            x = (x + Lrand()) * norm;
        }
        while (x >= 1);
        return x;
    }

    private sealed class ExitJump : Exception;

    private sealed record Frame(Cell Fcncell, Cell[] Args, int Nargs, Cell Retval);

    private sealed record Files(Bio? Fp, string? Fname, int Mode);
}
