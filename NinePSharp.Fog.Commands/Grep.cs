using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// grep(1), as 9front's grep: its expression parsed as grep.y's grammar from a lexer of bytes,
// compiled into a deterministic machine over bytes built as the input needs its states, and each
// file read through a buffer of 16K with 16K before it, so a longer line keeps its last 32K.
public sealed class Grep
{
    private const string Validflags = "bchiLlnsv";
    private const int Caselim = 7;
    private const uint Cbegin = Utf.RuneMax + 1;
    private const int Flshcnt = (1 << 9) - 1;
    private const int Half = 16 * 1024;

    private const int Cflag = 1 << 0;
    private const int Hflag = 1 << 1;
    private const int Iflag = 1 << 2;
    private const int Llflag = 1 << 3;
    private const int LLflag = 1 << 4;
    private const int Nflag = 1 << 5;
    private const int Sflag = 1 << 6;
    private const int Vflag = 1 << 7;
    private const int Bflag = 1 << 8;

    private const int Talt = 1;
    private const int Tbegin = 2;
    private const int Tcase = 3;
    private const int Tclass = 4;
    private const int Tend = 5;
    private const int Tor = 6;

    private const int LChar = 0;
    private const int LClass = 1;
    private const int LLparen = 2;
    private const int LRparen = 3;
    private const int LAlt = 4;
    private const int LStar = 5;
    private const int LPlus = 6;
    private const int LQues = 7;
    private const int LBegin = 8;
    private const int LEnd = 9;
    private const int LDot = 10;
    private const int LBad = 11;
    private const int LNewline = 12;
    private const int LEof = 13;

    private static readonly uint[] Tab1 = [0x007f, 0x07ff, 0xffff];
    private static readonly uint[] Tab2 = [0x003f, 0x0fff, 0x3ffff];

    private readonly Process process;
    private readonly bool[] flags = new bool[256];
    private readonly List<Re> follow = [];
    private readonly Biobuf bout;
    private string? filename;
    private string? pattern;
    private ushort gen;
    private byte[] input = [0];
    private int inputAt;
    private long lineno;
    private bool literal;
    private bool matched;
    private bool peekeof;
    private Biobuf? rein;
    private State? state0;
    private Re2 topre;
    private int nre;
    private int tok;
    private int yyval;
    private string yystr = null!;

    private Grep(Process process) => (this.process, bout) = (process, new Biobuf(process, 1));

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var g = new Grep(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'e':
                    g.flags['e'] = true;
                    g.lineno = 0;
                    await g.Str2topAsync(arguments.Value() ?? await g.UsageAsync());
                    break;
                case 'f':
                    g.flags['f'] = true;
                    g.filename = arguments.Value() ?? await g.UsageAsync();
                    int fd = 0;
                    try
                    {
                        fd = await process.OpenAsync(g.filename, NinePConstants.OREAD);
                    }
                    catch (SyscallException error)
                    {
                        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"grep: can't open {g.filename}: {error.Message}\n"));
                        process.Exits("open");
                    }

                    g.rein = new Biobuf(process, fd);
                    g.lineno = 1;
                    await g.Str2topAsync(g.filename);
                    break;
                default:
                    if (!Validflags.Contains(c, StringComparison.Ordinal))
                    {
                        await g.UsageAsync();
                    }

                    g.flags[c] = true;
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        int a = 0;
        if (!g.flags['f'] && !g.flags['e'])
        {
            if (args.Count == 0)
            {
                await g.UsageAsync();
            }

            await g.Str2topAsync(args[a++]);
        }

        g.state0 = g.Initstate(g.topre.Beg!);
        bool status = false;
        int files = args.Count - a;
        if (files <= 1)
        {
            status = await g.SearchAsync(files == 0 ? null : args[a], 0);
        }
        else
        {
            for (; a < args.Count; a++)
            {
                status |= await g.SearchAsync(args[a], Hflag);
            }
        }

        await g.bout.BflushAsync();
        process.Exits(status ? null : "no matches");
    }

    private static void Appendnext(Re a, Re? b)
    {
        while (a.Next is { } n)
        {
            a = n;
        }

        a.Next = b;
    }

    private static void Patchnext(Re? a, Re? b)
    {
        while (a is not null)
        {
            Re? n = a.Next;
            a.Next = b;
            a = n;
        }
    }

    private static Re2 Re2cat(Re2 a, Re2 b)
    {
        Patchnext(a.End, b.Beg);
        return new Re2 { Beg = a.Beg, End = b.End };
    }

    private static uint Next(byte[] s, ref int at)
    {
        at += Utf.CharToRune(out uint r, s.AsSpan(Math.Min(at, s.Length - 1)));
        return r;
    }

    private static int Countor(Re r)
    {
        int n = 0;
        for (; ;)
        {
            switch (r.Type)
            {
                case Tor:

                    // Stryker disable once Assignment : the count only chooses whether a case table is made, which matches as its alternation does
                    n += Countor(r.Alt!);
                    r = r.Next!;
                    continue;
                case Tclass:

                    // Stryker disable once Arithmetic : the count only chooses whether a case table is made, which matches as its alternation does
                    return n + (int)(r.Hi - r.Lo) + 1;
                default:
                    return n;
            }
        }
    }

    private async Task<string> UsageAsync()
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"usage: grep [-{Validflags}] [-e pattern] [-f patternfile] [file ...]\n"));
        process.Exits("usage");
        return default!;
    }

    private async Task YyerrorAsync(string e)
    {
        string where = filename is not null ? $"{filename}:{lineno}: " : pattern is not null ? $"{pattern}: " : string.Empty;
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"grep: {where}{e}\n"));
        process.Exits("syntax");
    }

    // error: grep's internal errors.
    private void Error(string s) => throw new InternalError(s);

    private async Task Str2topAsync(string p)
    {
        Re2 oldtop = topre;
        (input, inputAt) = ([.. Encoding.UTF8.GetBytes(p), 0], 0);
        if (p.Length == 0)
        {
            await YyerrorAsync("empty pattern");
        }

        if (!flags['f'])
        {
            pattern = p;
        }

        topre = default;
        (tok, peekeof, literal) = (-1, false, false);
        try
        {
            await ProgAsync();
        }
        catch (InternalError error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"grep: internal error: {error.Message}\n"));
            process.Exits(error.Message);
        }

        // yyparse sets topre or exits, so str2top's check that it did never fails. Fresh marks only
        // let addcase make its tables, which match as their alternations do.
        // Stryker disable once Update,Statement : as above
        gen++;
        if (oldtop.Beg is not null)
        {
            topre = Re2or(oldtop, topre);
        }
    }

    // getrec: the pattern's next byte, or the pattern file's, 0 at the end; folded with -i.
    private async Task<int> GetrecAsync()
    {
        int c;
        if (flags['f'])
        {
            // Beof, as a NUL does, ends the pattern file.
            c = Math.Max(await rein!.BgetcAsync(), 0);
        }
        else
        {
            c = inputAt < input.Length ? input[inputAt] : 0;
            inputAt++;
        }

        if (flags['i'] && c is >= 'A' and <= 'Z')
        {
            c += 'a' - 'A';
        }

        lineno += c == '\n' ? 1 : 0;
        return c;
    }

    private async Task<int> YylexAsync()
    {
        if (peekeof)
        {
            // Stryker disable once Boolean : a pattern's parse ends at the end it peeked at, and the next pattern's begins with it unset
            peekeof = false;
            return LEof;
        }

        int c = await GetrecAsync();
        if (literal)
        {
            if (c is not (0 or '\n'))
            {
                yyval = c;
                return LChar;
            }

            literal = false;
        }

        switch (c)
        {
            default:
                yyval = c;
                return LChar;
            case '\\':
                c = await GetrecAsync();
                yyval = c;
                return c == '\n' ? LNewline : LChar;
            case '[':
                break;
            case '(':
                return LLparen;
            case ')':
                return LRparen;
            case '|':
                return LAlt;
            case '*':
                return LStar;
            case '+':
                return LPlus;
            case '?':
                return LQues;
            case '^':
                return LBegin;
            case '$':
                return LEnd;
            case '.':
                return LDot;
            case 0:
                peekeof = true;
                return LNewline;
            case '\n':
                return LNewline;
        }

        // getclass: the class's bytes, a backslash kept with the byte it quotes; a negated class
        // takes the newline as one of its own.
        var q = new List<byte>();
        c = await GetrecAsync();
        if (c == '^')
        {
            q.AddRange("^\n-\n"u8.ToArray());
            c = await GetrecAsync();
        }

        for (; ;)
        {
            if (q.Count >= Half - 5)
            {
                Error("class too long");
            }

            if (c is ']' or 0)
            {
                break;
            }

            if (c == '\\')
            {
                q.Add((byte)c);
                c = await GetrecAsync();

                // Stryker disable once Statement : the NUL kept, the read past it is 0 again, and ends the class as this does
                if (c == 0)
                {
                    break;
                }
            }

            q.Add((byte)c);
            c = await GetrecAsync();
        }

        if (c == 0)
        {
            return LBad;
        }

        yystr = Encoding.UTF8.GetString([.. q]);
        return LClass;
    }

    private async Task<int> PeekAsync() => tok >= 0 ? tok : tok = await YylexAsync();

    private void Shift() => tok = -1;

    private Task SyntaxAsync() => YyerrorAsync("syntax error");

    // grep.y's prog: one or more expressions, each ended by newlines, matched anywhere on a line.
    // The empty prog is yacc's default first reduction, so a first token that cannot begin an
    // expression reduces it, and is an empty pattern.
    private async Task ProgAsync()
    {
        if (await PeekAsync() is not (LStar or LChar or LBegin or LEnd or LDot or LClass or LLparen))
        {
            await YyerrorAsync("empty pattern");
        }

        Re2 x = await Expr0Async();
        for (; ;)
        {
            if (await PeekAsync() != LNewline)
            {
                // Stryker disable once Statement,Block : the expression read after it is a syntax error all the same
                await SyntaxAsync();
            }

            while (await PeekAsync() == LNewline)
            {
                Shift();
            }

            if (await PeekAsync() == LEof)
            {
                break;
            }

            x = Re2or(x, await Expr0Async());
        }

        Re2 t;
        t.Beg = Ral(Tend);
        t.End = t.Beg;
        t = Re2cat(Re2star(Re2or(Re2char(0x00, '\n' - 1), Re2char('\n' + 1, 0xff))), t);
        t = Re2cat(x, t);
        topre = Re2cat(Re2star(Re2char(0x00, 0xff)), t);
    }

    // expr0: a leading * makes the rest of the expression literal.
    private async Task<Re2> Expr0Async()
    {
        if (await PeekAsync() == LStar)
        {
            Shift();
            literal = true;
        }

        return await Expr1Async();
    }

    private async Task<Re2> Expr1Async()
    {
        Re2 x = await Expr2Async();
        while (await PeekAsync() == LAlt)
        {
            Shift();
            x = Re2or(x, await Expr2Async());
        }

        return x;
    }

    private async Task<Re2> Expr2Async()
    {
        Re2 x = await Expr3Async();
        while (await PeekAsync() is LChar or LBegin or LEnd or LDot or LClass or LLparen)
        {
            x = Re2cat(x, await Expr3Async());
        }

        return x;
    }

    private async Task<Re2> Expr3Async()
    {
        Re2 x = await Expr4Async();
        for (int t; (t = await PeekAsync()) is LStar or LPlus or LQues;)
        {
            Shift();
            if (t == LStar)
            {
                x = Re2star(x);
            }
            else if (t == LPlus)
            {
                Re a = Ral(Talt);
                Patchnext(x.End, a);
                a.Alt = x.Beg;
                x.End = a;
            }
            else
            {
                Re a = Ral(Talt);
                a.Alt = x.Beg;
                Appendnext(x.End!, a);
                x.Beg = a;
            }
        }

        return x;
    }

    private async Task<Re2> Expr4Async()
    {
        Re2 x;
        switch (await PeekAsync())
        {
            case LChar:
                Shift();
                x.Beg = Ral(Tclass);
                x.Beg.Lo = x.Beg.Hi = (uint)yyval;
                x.End = x.Beg;
                return x;
            case LBegin or LEnd:
                x.Beg = Ral(tok == LBegin ? Tbegin : Tend);
                Shift();
                x.End = x.Beg;
                return x;
            case LDot:
                Shift();
                return Re2class("^\n");
            case LClass:
                Shift();
                return Re2class(yystr);
            case LLparen:
                Shift();
                x = await Expr1Async();
                if (await PeekAsync() != LRparen)
                {
                    await SyntaxAsync();
                }

                Shift();
                return x;
            default:
                await SyntaxAsync();
                return default;
        }
    }

    private Re Ral(int type)
    {
        // Stryker disable once Update : ids that differ all the same order the states alike
        return new Re { Id = nre++, Type = type };
    }

    private Re2 Re2star(Re2 a)
    {
        Re c = Ral(Talt);
        c.Alt = a.Beg;
        Patchnext(a.End, c);
        return new Re2 { Beg = c, End = c };
    }

    private Re2 Re2or(Re2 a, Re2 b)
    {
        Re c = Ral(Tor);
        (c.Alt, c.Next) = (b.Beg, a.Beg);
        Appendnext(b.End!, a.End);
        return new Re2 { Beg = c, End = b.End };
    }

    private Re2 Re2char(int c0, int c1)
    {
        Re c = Ral(Tclass);
        (c.Lo, c.Hi) = ((uint)(c0 & 0xff), (uint)(c1 & 0xff));
        return new Re2 { Beg = c, End = c };
    }

    // rclass: the runes from p0 to p1 as sequences of byte ranges, the range broken where the
    // length of its UTF-8 changes and where its bytes after the first do not cover their range.
    private Re2 Rclass(uint p0, uint p1)
    {
        if (p0 > p1)
        {
            return Re2char(0xff, 0xff);
        }

        foreach (uint m in Tab1)
        {
            // Stryker disable once Equality : a range from m itself is broken at the same place by tab2's masks
            if (p0 <= m && p1 > m)
            {
                return Re2or(Rclass(p0, m), Rclass(m + 1, p1));
            }
        }

        foreach (uint m in Tab2)
        {
            if ((p0 & ~m) != (p1 & ~m))
            {
                if ((p0 & m) != 0)
                {
                    return Re2or(Rclass(p0, p0 | m), Rclass((p0 | m) + 1, p1));
                }

                if ((p1 & m) != m)
                {
                    return Re2or(Rclass(p0, (p1 & ~m) - 1), Rclass(p1 & ~m, p1));
                }
            }
        }

        // The range is broken where the lengths change, so rclass's check that they agree holds.
        byte[] xc0 = new byte[Utf.UtfMax], xc1 = new byte[Utf.UtfMax];
        int n = Utf.RuneToChar(xc0, p0);
        Utf.RuneToChar(xc1, p1);

        Re2 x = Re2char(xc0[0], xc1[0]);
        for (int i = 1; i < n; i++)
        {
            x = Re2cat(x, Re2char(xc0[i], xc1[i]));
        }

        return x;
    }

    // re2class: the class's ranges sorted and merged where they overlap, or the gaps between them
    // for a negated class.
    private Re2 Re2class(string str)
    {
        byte[] s = [.. Encoding.UTF8.GetBytes(str), 0];
        int at = 0;
        bool nc = false;
        if (s[at] == '^')
        {
            nc = true;
            at++;
        }

        var pairs = new List<(uint Lo, uint Hi)>();
        uint p = Next(s, ref at);
        for (; ;)
        {
            if (p == '\\')
            {
                p = Next(s, ref at);
            }

            if (p == 0)
            {
                break;
            }

            pairs.Add((p, p));
            if (pairs.Count >= 100)
            {
                Error("class too big");
            }

            p = Next(s, ref at);
            if (p != '-')
            {
                continue;
            }

            p = Next(s, ref at);
            if (p == '\\')
            {
                p = Next(s, ref at);
            }

            if (p == 0)
            {
                break;
            }

            pairs[^1] = (pairs[^1].Lo, p);
            p = Next(s, ref at);
        }

        // Stryker disable once Logical,Arithmetic : ranges from the same rune merge into one whatever their order
        pairs.Sort((a, b) => (int)(a.Lo - b.Lo) is int n && n != 0 ? n : (int)(a.Hi - b.Hi));
        var q = new List<(uint Lo, uint Hi)>();
        for (int i = 0; i < pairs.Count; i++)
        {
            (uint lo, uint hi) = pairs[i];
            if (i == 0)
            {
                q.Add((lo, hi));

                // Stryker disable once Statement : the first range merges with itself to itself
                continue;
            }

            if (lo > hi)
            {
                continue;
            }

            // The ranges are in order from their starts, so one that starts at or past the last is never wholly before it.
            // Stryker disable once Equality : a range that touches the last or lies within it at its start excludes the same, merged or apart
            if (lo > q[^1].Hi || hi < q[^1].Lo)
            {
                q.Add((lo, hi));

                // Stryker disable once Statement : a range just added merges with itself to itself
                continue;
            }

            q[^1] = (Math.Min(q[^1].Lo, lo), Math.Max(q[^1].Hi, hi));
        }

        Re2 x;
        if (nc)
        {
            x = Rclass(0, q[0].Lo - 1);
            uint ov = q[0].Hi + 1;
            for (int i = 1; i < q.Count; i++)
            {
                x = Re2or(x, Rclass(ov, q[i].Lo - 1));
                ov = q[i].Hi + 1;
            }

            return Re2or(x, Rclass(ov, Utf.RuneMax));
        }

        x = Rclass(q[0].Lo, q[0].Hi);
        for (int i = 1; i < q.Count; i++)
        {
            x = Re2or(x, Rclass(q[i].Lo, q[i].Hi));
        }

        return x;
    }

    private Re Oralloc(int t, Re r, Re? b)
    {
        if (b is null)
        {
            return r;
        }

        Re a = Ral(t);
        (a.Alt, a.Next) = (r, b);
        return a;
    }

    private void Case1(Re c, Re r)
    {
        for (; ;)
        {
            switch (r.Type)
            {
                case Tor:
                    Case1(c, r.Alt!);
                    r = r.Next!;
                    continue;
                case Tclass:
                    for (uint n = r.Lo; n <= r.Hi; n++)
                    {
                        c.Cases![n] = Oralloc(Tor, r.Next!, c.Cases[n]);
                    }

                    return;
                default:
                    c.Next = Oralloc(Talt, r, c.Next);
                    return;
            }
        }
    }

    // addcase: an alternation of seven bytes or more made a table of the bytes it takes.
    private Re Addcase(Re r)
    {
        // Stryker disable once Equality : a node not made a table matches as its alternation does
        if (r.Gen == gen)
        {
            return r;
        }

        r.Gen = gen;
        switch (r.Type)
        {
            case Tor:
                int n = Countor(r);

                // Stryker disable once Equality,Boolean : whether a table is made, it matches as the alternation does
                if (n < Caselim)
                {
                    return r;
                }

                Re a = Ral(Tcase);
                a.Cases = new Re?[256];
                Case1(a, r);

                // Stryker disable once Equality : whether the cases are made tables, they match as their alternations do
                for (int i = 0; i < 256; i++)
                {
                    // Stryker disable once Equality : whether the cases are made tables, they match as their alternations do, and one no smaller is not
                    if (a.Cases[i] is { } c && Countor(c) < n)
                    {
                        a.Cases[i] = Addcase(c);
                    }
                }

                return a;
            case Talt:
                r.Next = Addcase(r.Next!);
                r.Alt = Addcase(r.Alt!);
                return r;
            default:
                return r;
        }
    }

    // fol1: what follows r on c into follow; matched when c ends a line the expression matches.
    // Each node is visited once, so follow never outgrows the nodes, nor finds a type it does not
    // know, as fol1's checks would have it.
    private void Fol1(Re? r, uint c)
    {
        while (r is not null && r.Gen != gen)
        {
            r.Gen = gen;
            switch (r.Type)
            {
                case Tcase:

                    // Stryker disable once Equality : c is a byte or Cbegin, never 256
                    if (c < 256 && r.Cases![c] is { } r1)
                    {
                        follow.Add(r1);
                    }

                    r = r.Next;
                    continue;
                case Talt or Tor:
                    Fol1(r.Alt, c);
                    r = r.Next;
                    continue;
                case Tbegin:
                    if (c is '\n' or Cbegin)
                    {
                        follow.Add(r.Next!);
                    }

                    return;
                case Tend:
                    if (c != '\n')
                    {
                        return;
                    }

                    if (r.Next is null)
                    {
                        matched = true;
                        return;
                    }

                    r = r.Next;
                    continue;
                default:
                    if (c >= r.Lo && c <= r.Hi)
                    {
                        follow.Add(r.Next!);
                    }

                    return;
            }
        }
    }

    private State Initstate(Re r)
    {
        // Stryker disable once Statement : the tables only make the alternations quicker to match
        Addcase(r);

        // Stryker disable once Statement : the follow set is empty before the first state is made
        follow.Clear();

        // Stryker disable once Update : this walk marks again every node addcase marked that a step from it reaches
        gen++;
        Fol1(r, Cbegin);
        follow.Add(r);

        // Stryker disable once Statement : an unordered state is never found again, and is made again the same
        follow.Sort((a, b) => a.Id.CompareTo(b.Id));
        return new State { Re = [.. follow] };
    }

    // increment: the state after s on c, found among those made, or made.
    private void Increment(State s, int c)
    {
        follow.Clear();
        gen++;
        matched = false;
        foreach (Re r in s.Re)
        {
            Fol1(r, (uint)c);
        }

        // Stryker disable once Statement : an unordered state is never found again, and is made again the same
        follow.Sort((a, b) => a.Id.CompareTo(b.Id));
        State? parent = null;

        // Stryker disable once Boolean : the loop sets it before it is read, state0 being made first
        bool left = false;
        for (State? t = state0; t is not null;)
        {
            int cmp = t.Re.Length.CompareTo(follow.Count);
            for (int i = 0; cmp == 0 && i < follow.Count; i++)
            {
                cmp = t.Re[i].Id.CompareTo(follow[i].Id);
            }

            if (cmp == 0 && matched != t.Match)
            {
                // Stryker disable once Conditional,Unary : either sign tells the two states apart
                cmp = matched ? 1 : -1;
            }

            if (cmp == 0)
            {
                s.Next[c] = t;
                return;
            }

            // Stryker disable once Equality : the state is found or made down either side alike
            (parent, left) = (t, cmp > 0);

            // Stryker disable once Conditional : the state is found or made down either side alike
            t = left ? t.Left : t.Right;
        }

        var made = new State { Re = [.. follow], Match = matched };

        // Stryker disable all : a state not kept in the tree is made again, and is the same
        if (left)
        {
            parent!.Left = made;
        }
        else
        {
            parent!.Right = made;
        }

        // Stryker restore all
        s.Next[c] = made;
    }

    // search: whether the file has a line to show, and its lines shown as the flags say.
    private async Task<bool> SearchAsync(string? file, int flag)
    {
        int fid = 0;
        if (file is null)
        {
            file = "stdin";
            flag |= Bflag;
        }
        else
        {
            try
            {
                fid = await process.OpenAsync(file, NinePConstants.OREAD);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"grep: can't open {file}: {error.Message}\n"));
                return false;
            }
        }

        flag |= flags['b'] ? Bflag : 0;
        flag |= flags['c'] ? Cflag : 0;
        flag &= flags['h'] ? ~Hflag : ~0;
        flag |= flags['i'] ? Iflag : 0;
        flag |= flags['l'] ? Llflag : 0;
        flag |= flags['L'] ? LLflag : 0;
        flag |= flags['n'] ? Nflag : 0;
        flag |= flags['s'] ? Sflag : 0;
        flag |= flags['v'] ? Vflag : 0;

        // u.pre and then u.buf, as one.
        byte[] u = new byte[2 * Half];
        State s = state0!;
        long line = 0, count = 0;
        bool eof = false, empty = true, nl = false;
        int lp = Half, bol = lp;
        for (; ;)
        {
            int n = Math.Min(lp - bol, Half);
            Array.Copy(u, bol, u, Half - n, n);
            bol = Half - n;
            try
            {
                ReadOnlyMemory<byte> got = await process.ReadAsync(fid, Half);
                got.CopyTo(u.AsMemory(Half));
                n = got.Length;
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"grep: read error on {file}: {error.Message}\n"));
                return count != 0;
            }

            // A file whose last line has no newline is given one, to show that line.
            if (n > 0)
            {
                (empty, nl) = (false, u[Half + n - 1] == '\n');
            }
            else if (!eof && !nl && !empty)
            {
                (u[Half], n, eof) = ((byte)'\n', 1, true);
            }

            if (n <= 0)
            {
                await process.CloseAsync(fid);
                if ((flag & Cflag) != 0)
                {
                    await BprintAsync((flag & Hflag) != 0 ? $"{file}:{count}\n" : $"{count}\n");
                }

                if (((flag & Llflag) != 0 && count != 0) || ((flag & LLflag) != 0 && count == 0))
                {
                    await BprintAsync($"{file}\n");
                }

                await bout.BflushAsync();
                return count != 0;
            }

            for (lp = Half; lp != Half + n;)
            {
                int c = u[lp];
                if ((flag & Iflag) != 0 && c is >= 'A' and <= 'Z')
                {
                    c += 'a' - 'A';
                }

                if (s.Next[c] is not { } ns)
                {
                    Increment(s, c);
                    continue;
                }

                lp++;
                s = ns;
                if (c != '\n')
                {
                    continue;
                }

                line++;
                if (s.Match == ((flag & Vflag) == 0))
                {
                    count++;
                    if ((flag & (Cflag | Sflag | Llflag | LLflag)) != 0)
                    {
                        bol = lp;
                        continue;
                    }

                    if ((flag & Hflag) != 0)
                    {
                        await BprintAsync($"{file}:");
                    }

                    if ((flag & Nflag) != 0)
                    {
                        await BprintAsync($"{line}: ");
                    }

                    // No newline of its own at the end of a file, unless the line is labeled.
                    await bout.BwriteAsync(u.AsMemory(bol, lp - bol - (eof && (flag & Hflag) == 0 ? 1 : 0)));
                    if ((flag & Bflag) != 0)
                    {
                        await bout.BflushAsync();
                    }
                }

                if ((line & Flshcnt) == 0)
                {
                    await bout.BflushAsync();
                }

                bol = lp;
            }
        }
    }

    private async Task BprintAsync(string s) => await bout.BwriteAsync(Encoding.UTF8.GetBytes(s));

    // A beginning and end of an expression's nodes.
    private struct Re2
    {
        public Re? Beg;
        public Re? End;
    }

    // A node: a byte range, the start or end of a line, an alternation, or a table of the bytes
    // an alternation takes; Id orders them as their addresses do.
    private sealed class Re
    {
        public int Id { get; init; }

        public int Type { get; init; }

        public ushort Gen { get; set; }

        public Re? Alt { get; set; }

        public Re?[]? Cases { get; set; }

        public uint Lo { get; set; }

        public uint Hi { get; set; }

        public Re? Next { get; set; }
    }

    // A state of the machine: the nodes it is at, whether a line ending in it matches, the
    // states after each byte, and its place in the tree of states.
    private sealed class State
    {
        public Re[] Re { get; init; } = [];

        public bool Match { get; init; }

        public State?[] Next { get; } = new State?[256];

        public State? Left { get; set; }

        public State? Right { get; set; }
    }

    private sealed class InternalError(string message) : Exception(message);
}
