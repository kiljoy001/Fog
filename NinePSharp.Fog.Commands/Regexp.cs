using System.Text;
using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// 9front's libregexp: regcomp's parse of an expression into a tree and its compilation into the
// instructions rregexec runs as threads over runes, leftmost first, and regsub. A Resub's start
// and end are indices into the runes, -1 for none. An error is a RegerrorException, whose
// message regerror prints before it exits "regerr".
public sealed class Regexp
{
    public const int Nsubexpm = 32;

    private const int OAny = 0;
    private const int OBol = 1;
    private const int OClass = 2;
    private const int OEol = 3;
    private const int OJmp = 4;
    private const int ONotnl = 5;
    private const int ORune = 6;
    private const int OSave = 7;
    private const int OSplit = 8;
    private const int OUnsave = 9;
    private const int LAny = 0;
    private const int LBol = 1;
    private const int LClass = 2;
    private const int LEnd = 3;
    private const int LEol = 4;
    private const int LLpar = 5;
    private const int LOr = 6;
    private const int LRep = 7;
    private const int LRpar = 8;
    private const int LRune = 9;
    private const int TAny = 0;
    private const int TBol = 1;
    private const int TCat = 2;
    private const int TClass = 3;
    private const int TEol = 4;
    private const int TNotnl = 5;
    private const int TOr = 6;
    private const int TPlus = 7;
    private const int TQues = 8;
    private const int TRune = 9;
    private const int TStar = 10;
    private const int TSub = 11;
    private const uint Runemax = Utf.RuneMax;

    // The instructions each node compiles to.
    private static readonly int[] Instrcnt = [2, 1, 0, 1, 1, 1, 2, 1, 1, 1, 2, 2];

    private readonly Reinst[] inst;

    private Regexp(string regstr, Reinst[] inst) => (Regstr, this.inst) = (regstr, inst);

    public string Regstr { get; }

    public static Regexp Regcomp(string s) => Regcomp1(s, false, false);

    public static Regexp Regcomplit(string s) => Regcomp1(s, false, true);

    public static Regexp Regcompnl(string s) => Regcomp1(s, true, false);

    // regerror: the message, and exits "regerr".
    public static async Task RegerrorAsync(Process process, string s)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"regerror: {s}\n"));
        process.Exits("regerr");
    }

    // rregsub: src with \0 to \9 replaced by those subexpressions of the match and & by the whole
    // of it, \\ by \ and \ before anything else by that; no more than dlen-1 runes.
    public static uint[] Rregsub(uint[] src, int dlen, uint[] str, Resub[]? match, int msize)
    {
        var dst = new List<uint>();
        int ep = dlen - 1;
        for (int s = 0; s < src.Length && src[s] != 0; s++)
        {
            uint c = src[s];
            if (c == '\\')
            {
                c = ++s < src.Length ? src[s] : 0;
                if (c is >= '0' and <= '9')
                {
                    int i = (int)(c - '0');
                    if (match is not null && i < msize && match[i].Ep >= 0)
                    {
                        Copy(dst, ep, str, match[i]);
                    }

                    continue;
                }

                if (c == 0)
                {
                    break;
                }
            }
            else if (c == '&')
            {
                if (match is not null && msize > 0 && match[0].Sp >= 0)
                {
                    Copy(dst, ep, str, match[0]);
                }

                continue;
            }

            if (dst.Count >= ep)
            {
                break;
            }

            dst.Add(c);
        }

        return [.. dst];
    }

    // rregexec: whether the program matches str, a string of runes ending at its first 0, from
    // sem[0]'s start and, if it has one at other than the end, as far as sem[0]'s end; the first
    // msize subexpressions of the leftmost match, the longest from there as its threads come, are
    // left in sem.
    public int Rregexec(uint[] str, Resub[]? sem, int msize)
    {
        msize = Math.Min(msize, Nsubexpm);
        foreach (Reinst ci in inst)
        {
            ci.Gen = 0;
        }

        var clist = new Queue();
        var nlist = new Queue();
        Rethread? avail = null;
        int gen = 0, matchgen = 0;
        int rsp = 0, rep = -1;
        uint endr = 0;
        if (sem is not null && msize > 0)
        {
            rsp = sem[0].Sp >= 0 ? sem[0].Sp : rsp;
            if (sem[0].Ep >= 0 && str[sem[0].Ep] != 0)
            {
                (rep, endr) = (sem[0].Ep, str[sem[0].Ep]);
                str[rep] = 0;
            }
        }

        for (uint r = '☺'; r != 0; rsp++)
        {
            r = str[rsp];
            gen++;
            Rethread t;
            if (matchgen == 0)
            {
                t = Take(ref avail);
                t.I = 0;
                Array.Fill(t.Sem, Resub.None, 0, msize);
                (t.Next, t.Gen) = (null, gen);
                clist.Add(t);
            }

            if (clist.Head is not { } first)
            {
                break;
            }

            t = first;
            Rethread? next;
            for (int i = t.I; ;)
            {
                // Again: each instruction once in a step, the first thread to reach it taking it.
                Reinst ci = inst[i];
                if (ci.Gen != gen)
                {
                    ci.Gen = gen;
                    switch (ci.Op)
                    {
                        case ORune when r != ci.R:
                            break;
                        case ORune or OAny:
                            (next, t.I, t.Next) = (t.Next, i + 1, null);
                            nlist.Add(t);
                            goto Next;
                        case OClass:
                            while (r >= inst[i].R && r > inst[i].R1)
                            {
                                i++;
                            }

                            if (r < inst[i].R)
                            {
                                break;
                            }

                            (next, t.I, t.Next) = (t.Next, inst[i].A, null);
                            nlist.Add(t);
                            goto Next;
                        case ONotnl or OBol or OEol:
                            bool on = ci.Op switch
                            {
                                ONotnl => r != '\n',
                                OBol => rsp == 0 || str[rsp - 1] == '\n',
                                _ => r == '\n' || (r == 0 && rep < 0),
                            };
                            if (!on)
                            {
                                break;
                            }

                            i++;
                            continue;
                        case OJmp:
                            i = ci.A;
                            continue;
                        case OSplit:
                            next = Take(ref avail);
                            next.I = ci.B;
                            Array.Copy(t.Sem, next.Sem, msize);
                            (next.Next, next.Gen, t.Next) = (t.Next, t.Gen, next);
                            i = ci.A;
                            continue;
                        case OSave:
                            if (ci.Sub < msize)
                            {
                                t.Sem[ci.Sub].Sp = rsp;
                            }

                            i++;
                            continue;
                        case OUnsave when ci.Sub == 0:
                            matchgen = t.Gen;
                            if (sem is not null && msize > 0)
                            {
                                Array.Copy(t.Sem, sem, msize);
                                sem[0].Ep = rsp;
                            }

                            break;
                        default:
                            if (ci.Sub < msize)
                            {
                                t.Sem[ci.Sub].Ep = rsp;
                            }

                            i++;
                            continue;
                    }
                }

                // Done: the thread is free again.
                (next, t.Next, avail) = (t.Next, avail, t);
            Next:
                if (next is null)
                {
                    break;
                }

                if (matchgen != 0 && next.Gen > matchgen)
                {
                    clist.Tail(avail);
                    avail = next;
                    break;
                }

                t = next;
                i = t.I;
            }

            (clist, nlist) = (nlist, clist);
            nlist.Clear();
        }

        if (rep >= 0)
        {
            str[rep] = endr;
        }

        return matchgen > 0 ? 1 : 0;
    }

    // strecpy of a match into what is left of dlen.
    private static void Copy(List<uint> dst, int ep, uint[] str, Resub m)
    {
        for (int k = m.Sp; k < m.Ep && str[k] != 0 && dst.Count < ep; k++)
        {
            dst.Add(str[k]);
        }
    }

    private static Rethread Take(ref Rethread? avail)
    {
        if (avail is null)
        {
            return new Rethread();
        }

        Rethread t = avail;
        avail = avail.Next;
        return t;
    }

    private static Regexp Regcomp1(string regstr, bool nl, bool lit)
    {
        var plex = new Parselex(regstr, lit);
        Renode parsetr = plex.Node(TSub, plex.E0(), null);
        var inst = new Reinst[plex.Instrs];
        for (int i = 0; i < inst.Length; i++)
        {
            inst[i] = new Reinst();
        }

        int sub = 0;
        Compile1(parsetr, inst, 0, ref sub, nl);
        return new Regexp(regstr, inst);
    }

    // compile1: the node's instructions from at on; where they end.
    private static int Compile1(Renode? renode, Reinst[] inst, int at, ref int sub, bool nl)
    {
        for (; ;)
        {
            if (renode is null)
            {
                return at;
            }

            Reinst c = inst[at];
            int i;
            switch (renode.Op)
            {
                case TClass:
                    (c.Op, c.R, c.R1, c.A) = (OClass, renode.R, renode.R1, at + 1 + renode.Nclass);
                    (renode, at) = (renode.Left, at + 1);
                    continue;
                case TCat:
                    at = Compile1(renode.Left, inst, at, ref sub, nl);
                    renode = renode.Right;
                    continue;
                case TOr:
                    (c.Op, c.A) = (OSplit, at + 1);
                    i = Compile1(renode.Left, inst, c.A, ref sub, nl);
                    c.B = i + 1;
                    inst[i].Op = OJmp;
                    return inst[i].A = Compile1(renode.Right, inst, c.B, ref sub, nl);
                case TStar:
                    (c.Op, c.A) = (OSplit, at + 1);
                    i = Compile1(renode.Left, inst, c.A, ref sub, nl);
                    c.B = i + 1;
                    (inst[i].Op, inst[i].A) = (OJmp, at);
                    return c.B;
                case TPlus:
                    i = at;
                    at = Compile1(renode.Left, inst, at, ref sub, nl);
                    (inst[at].Op, inst[at].A, inst[at].B) = (OSplit, i, at + 1);
                    return at + 1;
                case TQues:
                    (c.Op, c.A) = (OSplit, at + 1);
                    return c.B = Compile1(renode.Left, inst, c.A, ref sub, nl);
                case TSub:
                    int s = sub++;
                    (c.Op, c.Sub) = (OSave, s);
                    at = Compile1(renode.Left, inst, at + 1, ref sub, nl);
                    (inst[at].Op, inst[at].Sub) = (OUnsave, s);
                    return at + 1;
                case TAny:
                    if (!nl)
                    {
                        inst[at++].Op = ONotnl;
                    }

                    inst[at].Op = OAny;
                    return at + 1;
                case TRune:
                    (c.Op, c.R) = (ORune, renode.R);
                    return at + 1;
                default:
                    c.Op = renode.Op switch
                    {
                        TNotnl => ONotnl,
                        TEol => OEol,
                        _ => OBol,
                    };
                    return at + 1;
            }
        }
    }

    // A match of a subexpression, or of none.
    public struct Resub(int sp, int ep)
    {
        public static readonly Resub None = new(-1, -1);

        public int Sp { get; set; } = sp;

        public int Ep { get; set; } = ep;
    }

    // The message regerror prints for an expression that does not compile.
    public sealed class RegerrorException(string message) : Exception(message);

    private sealed class Reinst
    {
        public int Op { get; set; }

        public int Gen { get; set; }

        public int A { get; set; }

        public int B { get; set; }

        public uint R { get; set; }

        public uint R1 { get; set; }

        public int Sub { get; set; }
    }

    private sealed class Rethread
    {
        public int I { get; set; }

        public Resub[] Sem { get; } = new Resub[Nsubexpm];

        public Rethread? Next { get; set; }

        public int Gen { get; set; }
    }

    // A list of threads, added to at its tail.
    private sealed class Queue
    {
        private Rethread? last;

        public Rethread? Head { get; private set; }

        public void Add(Rethread t)
        {
            if (last is null)
            {
                Head = t;
            }
            else
            {
                last.Next = t;
            }

            last = t;
        }

        // *tail = list: the rest of the free threads hung after the last, as regexec leaves them.
        public void Tail(Rethread? list)
        {
            if (last is null)
            {
                Head = list;
            }
            else
            {
                last.Next = list;
            }
        }

        public void Clear() => (Head, last) = (null, null);
    }

    private sealed class Renode
    {
        public int Op { get; set; }

        public Renode? Left { get; set; }

        public Renode? Right { get; set; }

        public uint R { get; set; }

        public uint R1 { get; set; }

        public int Nclass { get; set; }
    }

    // The parse and its lexer: the expression's runes read one at a time, a backslash quoting the
    // next, or all of them quoted for regcomplit.
    private sealed class Parselex
    {
        private readonly byte[] raw;
        private readonly string orig;
        private readonly bool lit;
        private readonly List<uint> cpairs = [];
        private int rawexp;
        private uint rune;
        private bool peek;
        private int peeklex;
        private bool done;
        private bool literal;
        private bool nc;

        public Parselex(string regstr, bool lit)
        {
            raw = [.. Encoding.UTF8.GetBytes(regstr), 0];
            (orig, this.lit) = (regstr, lit);
        }

        public int Instrs { get; private set; }

        public Renode Node(int op, Renode? l, Renode? r)
        {
            Instrs += Instrcnt[op];
            return new Renode { Op = op, Left = l, Right = r };
        }

        public Renode E0()
        {
            Renode n = E1();
            while (Lex() == LOr)
            {
                n = Node(TOr, n, E1());
            }

            peek = true;
            return n;
        }

        // invert: a left-leaning run of concatenations made to lean right.
        private static Renode Invert(Renode n)
        {
            if (n.Op != TCat)
            {
                return n;
            }

            while (n.Left!.Op == TCat)
            {
                Renode n1 = n.Left;
                (n.Left, n1.Right) = (n1.Right, n);
                n = n1;
            }

            return n;
        }

        // snprint into regcomp's 128 bytes: no rune that would not fit.
        private static string Snprint(string s)
        {
            var b = new StringBuilder();
            int n = 0;
            foreach (System.Text.Rune r in s.EnumerateRunes())
            {
                if ((n += r.Utf8SequenceLength) > 127)
                {
                    break;
                }

                b.Append(r.ToString());
            }

            return b.ToString();
        }

        private Renode E1()
        {
            Renode n = E2();
            for (int sym; (sym = Lex()) is not (LEnd or LOr or LRpar);)
            {
                peek = true;
                n = Node(TCat, n, E2());
            }

            peek = true;
            return Invert(n);
        }

        private Renode E2()
        {
            Renode n = E3();
            while (Lex() == LRep)
            {
                n = Node(rune switch { '*' => TStar, '+' => TPlus, _ => TQues }, n, null);
            }

            peek = true;
            return n;
        }

        private Renode E3()
        {
            switch (Lex())
            {
                case LAny:
                    return Node(TAny, null, null);
                case LBol:
                    return Node(TBol, null, null);
                case LEol:
                    return Node(TEol, null, null);
                case LRune:
                    Renode n = Node(TRune, null, null);
                    n.R = rune;
                    return n;
                case LClass:
                    return nc ? Buildclassn() : Buildclass();
                case LLpar:
                    n = Node(TSub, E0(), null);
                    return Lex() == LRpar ? n : throw new RegerrorException(Snprint($"regexp {orig}: no matching parenthesis"));
                default:
                    throw new RegerrorException(Snprint(rune != 0 ? $"regexp {orig}: syntax error: {char.ConvertFromUtf32((int)Math.Min(rune, Runemax))}" : $"regexp {orig}: parsing error"));
            }
        }

        private void Getnextr()
        {
            literal = false;
            if (done)
            {
                rune = 0;
                return;
            }

            Read();
            if (rune == '\\')
            {
                Getnextrlit();
            }
        }

        private void Getnextrlit()
        {
            literal = true;
            if (done)
            {
                (literal, rune) = (false, 0);
                return;
            }

            Read();
        }

        // chartorune at rawexp, and done once the rune is the last.
        private void Read()
        {
            rawexp += Utf.CharToRune(out rune, raw.AsSpan(Math.Min(rawexp, raw.Length - 1)));
            done = rawexp >= raw.Length - 1;
        }

        private int Lex()
        {
            if (peek)
            {
                peek = false;
                return peeklex;
            }

            if (lit)
            {
                Getnextrlit();
            }
            else
            {
                Getnextr();
            }

            if (literal)
            {
                return peeklex = LRune;
            }

            if (rune == '[')
            {
                Getclass();
                return peeklex = LClass;
            }

            return peeklex = rune switch
            {
                0 => LEnd,
                '*' or '?' or '+' => LRep,
                '|' => LOr,
                '.' => LAny,
                '(' => LLpar,
                ')' => LRpar,
                '^' => LBol,
                '$' => LEol,
                _ => LRune,
            };
        }

        // The class's ranges, each from its low rune to its high, sorted from the highest down and
        // merged where they touch or overlap.
        private void Getclass()
        {
            nc = false;
            Getnextrlit();
            if (rune == '^')
            {
                nc = true;
                Getnextrlit();
            }

            var pairs = new List<(uint Lo, uint Hi)>();
            while (rune != ']')
            {
                if (rune == '-')
                {
                    throw new RegerrorException("Malformed class");
                }

                if (rune == '\\')
                {
                    Getnextrlit();
                }

                if (rune == 0)
                {
                    throw new RegerrorException("No closing ] for class");
                }

                uint lo = rune, hi = lo;
                Getnextrlit();
                if (rune == '-')
                {
                    Getnextrlit();
                    if (rune is ']' or '-')
                    {
                        throw new RegerrorException("Malformed class");
                    }

                    if (rune == '\\')
                    {
                        Getnextrlit();
                    }

                    (lo, hi) = lo > rune ? (rune, lo) : (lo, rune);
                    Getnextrlit();
                }

                if (pairs.Count >= 200)
                {
                    throw new RegerrorException("Class too big\n");
                }

                pairs.Add((lo, hi));
            }

            pairs.Sort((a, b) => a.Lo != b.Lo ? b.Lo.CompareTo(a.Lo) : b.Hi.CompareTo(a.Hi));
            cpairs.Clear();
            foreach ((uint lo, uint hi) in pairs)
            {
                if (cpairs.Count == 0 || hi < cpairs[^2] - 1)
                {
                    cpairs.Add(lo);
                    cpairs.Add(hi);
                    continue;
                }

                cpairs[^2] = lo;
                cpairs[^1] = Math.Max(cpairs[^1], hi);
            }
        }

        // A negated class: the gaps around the ranges, from the highest down, and no newline.
        private Renode Buildclassn()
        {
            int i = 0;
            Renode n = Node(TClass, null, null);
            (n.R, n.R1, n.Nclass) = (cpairs.Count > 0 ? cpairs[1] + 1 : 1, Runemax, i++);
            for (int p = 0; p < cpairs.Count; p += 2)
            {
                n = Node(TClass, n, null);
                (n.R, n.R1, n.Nclass) = (p + 3 < cpairs.Count ? cpairs[p + 3] + 1 : 0, cpairs[p] - 1, i++);
            }

            n.R = 0;
            return Node(TCat, Node(TNotnl, null, null), n);
        }

        private Renode Buildclass()
        {
            int i = 0;
            Renode n = Node(TClass, null, null);
            (n.R, n.Nclass) = (Runemax + 1, i++);
            for (int p = 0; p < cpairs.Count; p += 2)
            {
                n = Node(TClass, n, null);
                (n.R, n.R1, n.Nclass) = (cpairs[p], cpairs[p + 1], i++);
            }

            return n;
        }
    }
}
