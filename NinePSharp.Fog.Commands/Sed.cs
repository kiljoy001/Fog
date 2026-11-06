using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// sed(1), as 9front's sed.c: its commands compiled into a table, its lines, hold space and
// texts in fixed buffers of runes, its regular expressions libregexp's, and its output through
// Biobufs whose errors are fatal, flushed at its exit as libbio's atexit flushes them.
public sealed class Sed
{
    private const int Maxcmds = 512;
    private const int Addsize = 10000;
    private const int Maxadds = 20;
    private const int Lbsize = 32768;
    private const int Labsize = 50;
    private const int Maxsub = 10;
    private const int Maxfiles = 120;
    private const uint Runemax = Utf.RuneMax;

    private readonly Process process;
    private readonly string argv0;
    private readonly SedCom[] pspace = new SedCom[Maxcmds];
    private readonly uint[] addspace = new uint[Addsize + 1];
    private readonly List<int> abuf = [];
    private readonly uint[] genbuf = new uint[Lbsize + 2];
    private readonly uint[] linebuf = new uint[Lbsize + 2];
    private readonly uint[] holdsp = new uint[Lbsize + 2];
    private readonly List<string> fname = [string.Empty];
    private readonly List<Biobuf> fcode = [];
    private readonly Label[] ltab = new Label[Labsize];
    private readonly List<int> cmpend = [];
    private readonly Queue<string?> fhead = new();
    private readonly Regexp.Resub[] subexp = new Regexp.Resub[Maxsub];
    private readonly Biobuf fout;
    private int rep;
    private int dollars;
    private Regexp? lastre;
    private int addp;
    private bool progFile;
    private byte[]? progCurr;
    private int progAt;
    private Biobuf? progBp;
    private int loc1;
    private int loc2;
    private uint seof;
    private int spend;
    private int cp;
    private int hspend;
    private bool nflag;
    private bool gflag;
    private bool uflag;
    private bool dolflag;
    private bool sflag;
    private bool jflag;
    private bool delflag;
    private int lnum;
    private Biobuf? f;

    // Stryker disable once Unary : opendata sets it before gline reads it
    private int ffd = -1;
    private long peekc;
    private int lab = 1;
    private int depth;

    private Sed(Process process, string argv0)
    {
        (this.process, this.argv0) = (process, argv0);
        for (int i = 0; i < pspace.Length; i++)
        {
            pspace[i] = new SedCom();
        }

        for (int i = 0; i < ltab.Length; i++)
        {
            ltab[i] = new Label();
        }

        fout = new Biobuf(process, 1) { Errorf = SysfatalAsync };
        fcode.Add(fout);
    }

    private enum Cmd
    {
        None,
        A,
        B,
        C,
        CD,
        CN,
        CP,
        D,
        Eq,
        G,
        CG,
        H,
        CH,
        I,
        L,
        N,
        P,
        Q,
        R,
        S,
        T,
        W,
        X,
        Y,
    }

    private enum At
    {
        None,
        Dol,
        Line,
        Re,
        Last,
    }

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var sed = new Sed(process, argv[0]);
        if (argv.Count == 1)
        {
            process.Exits(null);
        }

        bool compfl = false;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'e' or 'f':
                    if (arguments.Argc <= 1)
                    {
                        await sed.QuitAsync(c == 'e' ? "missing pattern" : "no pattern-file");
                    }

                    await sed.NewfileAsync(c == 'f', arguments.Value());
                    await sed.FcompAsync();
                    compfl = true;
                    break;
                case 'g':
                    sed.gflag = true;
                    break;
                case 'n':
                    sed.nflag = true;
                    break;
                case 'u':
                    sed.uflag = true;
                    break;
                case 'E' or 'r':
                    break;
                default:
                    await sed.QuitAsync($"Unknown flag: {c}");
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        int a = 0;
        if (!compfl)
        {
            if (args.Count == 0)
            {
                await sed.QuitAsync("missing pattern");
            }

            await sed.NewfileAsync(false, args[a++]);
            await sed.FcompAsync();
        }

        if (sed.depth != 0)
        {
            await sed.QuitAsync("Too many {'s");
        }

        sed.ltab[0].Address = sed.rep;
        await sed.DechainAsync();
        if (a == args.Count)
        {
            sed.fhead.Enqueue(null);
        }

        for (; a < args.Count; a++)
        {
            sed.fhead.Enqueue(args[a]);
        }

        await sed.ExecuteAsync();
        await sed.ExitsAsync(null);
    }

    private static string S(uint[] buf, int at)
    {
        var s = new StringBuilder();
        for (int i = at; i < buf.Length && buf[i] != 0; i++)
        {
            s.Append(char.ConvertFromUtf32((int)Math.Min(buf[i] is >= 0xD800 and <= 0xDFFF ? Utf.RuneError : buf[i], Runemax)));
        }

        return s.ToString();
    }

    // The rune at i in a buffer, or 0 past it, where sed.c reads what follows.
    // Stryker disable once Conditional,Equality : what sed reads ends at the line's NUL, never past it
    private static uint R(uint[] buf, int i) => i < buf.Length ? buf[i] : 0;

    // trans: how l shows a rune it cannot print.
    private static string Trans(uint c) => c switch
    {
        '\b' => "\\b",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\\' => "\\\\",
        _ => $"\\x{c & 0xFFFF:x4}",
    };

    // to, from at, as far as and including its NUL; where the NUL went.
    private static int Copy(uint[] to, int at, uint[] from, int start)
    {
        int p1 = at, p2 = start;
        while ((to[p1++] = from[p2++]) != 0)
        {
        }

        return p1 - 1;
    }

    // from, after to's end, until its NUL or to's end.
    private static int Append(uint[] to, int at, uint[] from, int end)
    {
        int p1 = at, p2 = 0;
        while ((to[p1++] = from[p2++]) != 0)
        {
            if (p1 >= end)
            {
                break;
            }
        }

        return p1 - 1;
    }

    private static async Task PutlineAsync(Biobuf bp, uint[] buf, int n)
    {
        for (int i = 0; i < n; i++)
        {
            await bp.BputruneAsync(buf[i]);
        }

        await bp.BputcAsync((byte)'\n');
    }

    private uint L(int i) => R(linebuf, i);

    private string Line() => S(linebuf, 0);

    // quit: sed: and the message, no more than 255 bytes of them, then exits "error".
    private async Task QuitAsync(string message)
    {
        byte[] msg = Encoding.UTF8.GetBytes($"sed: {message}\n");
        int n = 0;
        for (int w; n < msg.Length && n + (w = Utf.CharToRune(out _, msg.AsSpan(n))) <= 255; n += w)
        {
        }

        await process.WriteAsync(2, msg.AsMemory(0, n).ToArray());
        await ExitsAsync("error");
    }

    // Blethal's sysfatal: argv0 and the message.
    private async Task SysfatalAsync(string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: {message}\n"));
        await ExitsAsync(message);
    }

    // exits, after libbio's atexit has flushed each output.
    private async Task ExitsAsync(string? status)
    {
        foreach (Biobuf bp in fcode.ToArray())
        {
            await bp.BflushAsync();
        }

        process.Exits(status);
    }

    private async Task NewfileAsync(bool file, string? name)
    {
        progFile = file;
        if (!file)
        {
            // Stryker disable once Conditional : arguments gives a name for each -e, never none
            (progCurr, progAt) = (name is null ? null : [.. Encoding.UTF8.GetBytes(name), 0], 0);
            return;
        }

        // Stryker disable once Unary : open's failure quits before fd is read
        int fd = -1;
        try
        {
            fd = await process.OpenAsync(name!, NinePConstants.OREAD);
        }
        catch (SyscallException)
        {
            await QuitAsync($"Cannot open pattern-file: {name}\n");
        }

        // Stryker disable once Conditional : the program is read before anything is written that a flush could send
        progBp = new Biobuf(process, fd) { Errorf = SysfatalAsync, Iof = uflag ? FlushoutAsync : null };
    }

    private async Task FlushoutAsync()
    {
        foreach (Biobuf bp in fcode.ToArray())
        {
            await bp.BflushAsync();
        }
    }

    // getrune: the program's next rune, a newline at the end of an argument, or -1.
    private async Task<long> GetruneAsync()
    {
        if (progFile)
        {
            return await progBp!.BgetruneAsync();
        }

        if (progCurr is null)
        {
            return -1;
        }

        if (progCurr[progAt] == 0)
        {
            progCurr = null;
            return '\n';
        }

        progAt += Utf.CharToRune(out uint r, progCurr.AsSpan(progAt));
        return r;
    }

    // rline: a line of the program into linebuf, a backslash keeping the rune after it; -1 at its
    // end, which leaves a last line without a newline uncompiled.
    private async Task<int> RlineAsync()
    {
        int buf = 0;
        for (long c; (c = await GetruneAsync()) >= 0;)
        {
            uint r = (uint)c;
            if (r == '\\')
            {
                if (buf <= Lbsize)
                {
                    linebuf[buf++] = r;
                }

                // Stryker disable once Block : a line the program's end cuts short is not compiled either way
                if ((c = await GetruneAsync()) < 0)
                {
                    // Stryker disable once Statement : a line the program's end cuts short is not compiled either way
                    break;
                }

                r = (uint)c;
            }
            else if (r == '\n')
            {
                linebuf[buf] = 0;
                return 1;
            }

            if (buf <= Lbsize)
            {
                linebuf[buf++] = r;
            }
        }

        linebuf[buf] = 0;
        return -1;
    }

    private async Task FcompAsync()
    {
        // Stryker disable once Equality : rline gives 1 or -1
        while (await RlineAsync() >= 0)
        {
            cp = 0;
        comploop:
            while (L(cp) is ' ' or '\t')
            {
                cp++;
            }

            if (L(cp) is 0 or '#')
            {
                continue;
            }

            if (L(cp) == ';')
            {
                cp++;
                goto comploop;
            }

            SedCom com = pspace[rep];
            await AddressAsync(com.Ad1);
            if (com.Ad1.Type != At.None)
            {
                // Stryker disable once Assignment : dollars is only asked whether it is 0
                dollars += com.Ad1.Type == At.Dol ? 1 : 0;
                if (com.Ad1.Type == At.Last)
                {
                    if (lastre is null)
                    {
                        await QuitAsync("First RE may not be null");
                    }

                    (com.Ad1.Type, com.Ad1.Rp) = (At.Re, lastre);
                }

                // Stryker disable once Block : a command's second address is none until it is set
                if (L(cp) is ',' or ';')
                {
                    cp++;
                    await AddressAsync(com.Ad2);
                    if (com.Ad2.Type == At.Last)
                    {
                        (com.Ad2.Type, com.Ad2.Rp) = (At.Re, lastre);
                    }
                }
                else
                {
                    com.Ad2.Type = At.None;
                }
            }

        swit:
            while (L(cp) is ' ' or '\t')
            {
                cp++;
            }

            switch (L(cp++))
            {
                default:
                    await QuitAsync($"Unrecognized command: {Line()}");
                    break;
                case '!':
                    com.Negfl = true;
                    goto swit;
                case '{':
                    (com.Command, com.Negfl) = (Cmd.B, !com.Negfl);
                    cmpend.Add(rep);
                    depth++;
                    if (++rep >= Maxcmds)
                    {
                        await QuitAsync($"Too many commands: {Line()}");
                    }

                    if (L(cp) == 0)
                    {
                        continue;
                    }

                    goto comploop;
                case '}':
                    if (com.Ad1.Type != At.None)
                    {
                        await QuitAsync($"No addresses allowed: {Line()}");
                    }

                    if (--depth < 0)
                    {
                        await QuitAsync("Too many }'s");
                    }

                    pspace[cmpend[depth]].Lb1 = rep;
                    cmpend.RemoveAt(depth);
                    if (L(cp) == 0)
                    {
                        continue;
                    }

                    goto comploop;
                case '=':
                    com.Command = Cmd.Eq;
                    await OneAddressAsync(com);
                    break;
                case ':':
                    if (com.Ad1.Type != At.None)
                    {
                        await QuitAsync($"No addresses allowed: {Line()}");
                    }

                    while (L(cp) == ' ')
                    {
                        cp++;
                    }

                    var name = new StringBuilder();
                    while (L(cp) is not (0 or ';' or ' ' or '\t' or '#'))
                    {
                        name.Append(S([L(cp++)], 0));
                        if (name.ToString().EnumerateRunes().Count() >= 8)
                        {
                            await QuitAsync($"Label too long: {Line()}");
                        }
                    }

                    ltab[lab].Uninm = name.ToString();
                    if (name.Length == 0)
                    {
                        await QuitAsync($": command garbled: {Line()}");
                    }

                    Label lpt;
                    if (Search() is { } found)
                    {
                        lpt = found;
                        if (lpt.Address >= 0)
                        {
                            await QuitAsync($"Duplicate labels: {Line()}");
                        }
                    }
                    else
                    {
                        lpt = ltab[lab];
                        lpt.Chain = -1;
                        if (++lab >= Labsize)
                        {
                            await QuitAsync($"Too many labels: {Line()}");
                        }
                    }

                    lpt.Address = rep;
                    if (L(cp) == '#')
                    {
                        continue;
                    }

                    rep--;
                    break;
                case 'a' or 'c' or 'i':
                    char which = (char)L(cp - 1);
                    com.Command = which == 'a' ? Cmd.A : which == 'c' ? Cmd.C : Cmd.I;
                    if (which != 'c')
                    {
                        await OneAddressAsync(com);
                    }

                    // Stryker disable once Conditional : only a newline may follow, and a rune skipped that is not one is garbled all the same
                    cp += L(cp) == '\\' ? 1 : 0;
                    if (L(cp++) != '\n')
                    {
                        await QuitAsync($"{which} command garbled: {Line()}");
                    }

                    com.Text = addp;
                    addp = await StextAsync(addp);
                    break;
                case 'g':
                    com.Command = Cmd.G;
                    break;
                case 'G':
                    com.Command = Cmd.CG;
                    break;
                case 'h':
                    com.Command = Cmd.H;
                    break;
                case 'H':
                    com.Command = Cmd.CH;
                    break;
                case 't' or 'b':
                    com.Command = L(cp - 1) == 't' ? Cmd.T : Cmd.B;
                    await JumpAsync(com);
                    break;
                case 'n':
                    com.Command = Cmd.N;
                    break;
                case 'N':
                    com.Command = Cmd.CN;
                    break;
                case 'p':
                    com.Command = Cmd.P;
                    break;
                case 'P':
                    com.Command = Cmd.CP;
                    break;
                case 'r':
                    com.Command = Cmd.R;
                    await OneAddressAsync(com);
                    if (L(cp++) != ' ')
                    {
                        await QuitAsync($"r command garbled: {Line()}");
                    }

                    com.Text = addp;
                    addp = await StextAsync(addp);
                    break;
                case 'd':
                    com.Command = Cmd.D;
                    break;
                case 'D':
                    (com.Command, com.Lb1) = (Cmd.CD, 0);
                    break;
                case 'q':
                    com.Command = Cmd.Q;
                    await OneAddressAsync(com);
                    break;
                case 'l':
                    com.Command = Cmd.L;
                    break;
                case 's':
                    com.Command = Cmd.S;
                    seof = L(cp++);
                    if ((com.Re1 = await CompileAsync()) is null)
                    {
                        if (lastre is null)
                        {
                            await QuitAsync("First RE may not be null");
                        }

                        com.Re1 = lastre;
                    }

                    com.Rhs = addp;
                    if (Compsub(addp) is not { } end)
                    {
                        await QuitAsync($"s command garbled: {Line()}");
                        break;
                    }

                    addp = end;
                    if (L(cp) == 'g')
                    {
                        cp++;
                        com.Gfl = true;
                    }
                    else if (gflag)
                    {
                        com.Gfl = true;
                    }

                    if (L(cp) == 'p')
                    {
                        cp++;
                        com.Pfl = 1;
                    }

                    if (L(cp) == 'P')
                    {
                        cp++;
                        com.Pfl = 2;
                    }

                    if (L(cp) == 'w')
                    {
                        cp++;
                        if (L(cp++) != ' ')
                        {
                            await QuitAsync($"s command garbled: {Line()}");
                        }

                        string file = Text();
                        if (fname.LastIndexOf(file) is int known and >= 0)
                        {
                            com.Fcode = fcode[known];
                            goto done;
                        }

                        if (fcode.Count >= Maxfiles)
                        {
                            await QuitAsync("Too many files in w commands 1");
                        }

                        com.Fcode = await OpenFileAsync(file);
                    }

                    break;
                case 'w':
                    com.Command = Cmd.W;
                    if (L(cp++) != ' ')
                    {
                        await QuitAsync($"w command garbled: {Line()}");
                    }

                    string wfile = Text();
                    if (fname.LastIndexOf(wfile) is int had and >= 0)
                    {
                        com.Fcode = fcode[had];
                        goto done;
                    }

                    if (fcode.Count >= Maxfiles)
                    {
                        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"sed: Too many files in w commands 2 \nnfiles = {fcode.Count}; MAXF = {Maxfiles}\n"));
                        await ExitsAsync("error");
                    }

                    com.Fcode = await OpenFileAsync(wfile);
                    break;
                case 'x':
                    com.Command = Cmd.X;
                    break;
                case 'y':
                    com.Command = Cmd.Y;
                    seof = L(cp++);
                    if (!Ycomp(com))
                    {
                        await QuitAsync($"y command garbled: {Line()}");
                    }

                    break;
            }

        done:
            if (++rep >= Maxcmds)
            {
                await QuitAsync($"Too many commands, last: {Line()}");
            }

            if (L(cp++) != 0)
            {
                if (L(cp - 1) == ';')
                {
                    goto comploop;
                }

                await QuitAsync($"{S(linebuf, cp - 1)} command garbled: {Line()}");
            }
        }
    }

    private async Task OneAddressAsync(SedCom com)
    {
        if (com.Ad2.Type != At.None)
        {
            await QuitAsync($"Only one address allowed: {Line()}");
        }
    }

    // b and t: to the label after them, or to the end without one, a label not yet defined
    // chaining the branches to it until dechain.
    private async Task JumpAsync(SedCom com)
    {
        while (L(cp) == ' ')
        {
            cp++;
        }

        // Stryker disable once Block : a branch left at -1 ends the commands as a branch to the end does, and the empty name finds the end's label
        if (L(cp) is 0 or ';')
        {
            // Stryker disable once Statement : a branch left at -1 ends the commands as a branch to the end does
            Chain(ltab[0]);

            // Stryker disable once Statement : the name is empty, and branches to the end all the same
            return;
        }

        var name = new StringBuilder();
        for (uint t; (t = L(cp++)) is not (0 or ';');)
        {
            name.Append(S([t], 0));
            if (name.ToString().EnumerateRunes().Count() >= 8)
            {
                await QuitAsync($"Label too long: {Line()}");
            }
        }

        cp--;
        ltab[lab].Uninm = name.ToString();
        if (Search() is { } lpt)
        {
            if (lpt.Address >= 0)
            {
                com.Lb1 = lpt.Address;
            }
            else if (lpt.Chain >= 0)
            {
                Chain(lpt);
            }

            return;
        }

        (ltab[lab].Chain, ltab[lab].Address) = (rep, -1);
        if (++lab >= Labsize)
        {
            await QuitAsync($"Too many labels: {Line()}");
        }
    }

    // This command at the end of the label's chain of branches.
    private void Chain(Label label)
    {
        if (label.Chain < 0)
        {
            label.Chain = rep;

            // Stryker disable once Statement : the link to itself is replaced as dechain resolves the chain
            return;
        }

        int pt = label.Chain;

        // Stryker disable once Equality : the first command never follows another in a chain
        while (pspace[pt].Lb1 >= 0)
        {
            pt = pspace[pt].Lb1;
        }

        pspace[pt].Lb1 = rep;
    }

    private async Task<Biobuf> OpenFileAsync(string name)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync(name, NinePConstants.OWRITE);
        }
        catch (SyscallException)
        {
            // Stryker disable once Unary : create or quit sets it before it is read
            fd = -1;
            try
            {
                fd = await process.CreateAsync(name, NinePConstants.OWRITE, 0b110_110_110);
            }
            catch (SyscallException)
            {
                await QuitAsync($"Cannot create {name}");
            }
        }

        var bp = new Biobuf(process, fd) { Errorf = SysfatalAsync };
        await process.SeekAsync(fd, 0, 2);
        fname.Add(name);
        fcode.Add(bp);
        return bp;
    }

    // compsub: the right side of an s, to the delimiter, into the text space; a backslash marked by
    // Runemax before the rune it quotes, \n a newline; null when it does not end or fit.
    private int? Compsub(int rhs)
    {
        for (uint r; (r = L(cp++)) != 0;)
        {
            if (r == '\\')
            {
                // Stryker disable once Equality,Block : the rune after a backslash at the limit is refused just after
                if (rhs >= Addsize)
                {
                    return null;
                }

                addspace[rhs++] = Runemax;
                r = L(cp++);
                r = r == 'n' ? '\n' : r;
            }
            else if (r == seof)
            {
                if (rhs >= Addsize)
                {
                    return null;
                }

                addspace[rhs++] = 0;
                return rhs;
            }

            // Stryker disable once Equality,Block : the delimiter after a rune at the limit is refused just after
            if (rhs >= Addsize)
            {
                return null;
            }

            addspace[rhs++] = r;
        }

        return null;
    }

    // compile: the expression to the delimiter, in 512 bytes of UTF-8, \n a newline; null for an
    // empty one, which is the last again.
    private async Task<Regexp?> CompileAsync()
    {
        uint c;
        if ((c = L(cp++)) == seof)
        {
            return null;
        }

        var expbuf = new List<byte>();
        do
        {
            if (c is 0 or '\n')
            {
                // Stryker disable once Statement : the expression read on past the line's end is too much text at put all the same
                await QuitAsync($"Too much text: {Line()}");
            }

            if (c == '\\')
            {
                await PutAsync(expbuf, c);
                if ((c = L(cp++)) == 'n')
                {
                    c = '\n';
                }
            }

            await PutAsync(expbuf, c);
        }
        while ((c = L(cp++)) != seof);

        try
        {
            // A C string, as far as a NUL a backslash may have put in it.
            int nul = expbuf.IndexOf(0);

            // Stryker disable once Equality : an expression never begins with a NUL, which would have ended its line
            return lastre = Regexp.Regcomp(Encoding.UTF8.GetString([.. nul < 0 ? expbuf : expbuf[..nul]]));
        }
        catch (Regexp.RegerrorException)
        {
            // sed's regerror.
            await QuitAsync($"r.e.-using command garbled: {Line()}");
            return null;
        }
    }

    private async Task PutAsync(List<byte> expbuf, uint c)
    {
        if (expbuf.Count >= 512)
        {
            await QuitAsync($"Too much text: {Line()}");
        }

        byte[] b = new byte[Utf.UtfMax];
        expbuf.AddRange(b[..Utf.RuneToChar(b, c)]);
    }

    private async Task AddressAsync(Addr ap)
    {
        uint c;
        if ((c = L(cp++)) == '$')
        {
            ap.Type = At.Dol;
        }
        else if (c == '/')
        {
            seof = c;
            ap.Type = (ap.Rp = await CompileAsync()) is null ? At.Last : At.Re;
        }
        else if (c is >= '0' and <= '9')
        {
            int lno = (int)(c - '0');
            while (L(cp) is >= '0' and <= '9')
            {
                lno = unchecked((lno * 10) + (int)L(cp++) - '0');
            }

            if (lno == 0)
            {
                await QuitAsync("line number 0 is illegal");
            }

            (ap.Type, ap.Line) = (At.Line, lno);
        }
        else
        {
            cp--;
            ap.Type = At.None;
        }
    }

    // text: a file name, its blanks and those after a newline skipped, \ quoting the next rune.
    private string Text()
    {
        var p = new StringBuilder();
        while (L(cp) is ' ' or '\t')
        {
            cp++;
        }

        while (L(cp) != 0)
        {
            uint r = L(cp++);

            // Stryker disable once Block : a backslash at the end of the name adds a NUL the name ends at anyway
            if (r == '\\' && (r = L(cp++)) == 0)
            {
                // Stryker disable once Statement : a backslash at the end of the name adds a NUL the name ends at anyway
                break;
            }

            if (r == '\n')
            {
                while (L(cp) is ' ' or '\t')
                {
                    cp++;
                }
            }

            p.Append(S([r], 0));
        }

        return p.ToString();
    }

    // stext: the text of a, c, i or r into the text space, as text reads it, where it ends.
    private async Task<int> StextAsync(int p)
    {
        while (L(cp) is ' ' or '\t')
        {
            cp++;
        }

        while (L(cp) != 0)
        {
            // Stryker disable once Block : a backslash at the end of the text adds a NUL the text ends at anyway
            if (L(cp) == '\\' && L(++cp) == 0)
            {
                // Stryker disable once Statement : a backslash at the end of the text adds a NUL the text ends at anyway
                break;
            }

            if (p >= Addsize - 1)
            {
                await QuitAsync($"Too much text: {Line()}");
            }

            if ((addspace[p++] = L(cp++)) == '\n')
            {
                while (L(cp) is ' ' or '\t')
                {
                    cp++;
                }
            }
        }

        addspace[p++] = 0;
        return p;
    }

    private Label? Search() => ltab.Take(lab).FirstOrDefault(l => l.Uninm == ltab[lab].Uninm);

    private async Task DechainAsync()
    {
        for (int l = 0; l < lab; l++)
        {
            Label lptr = ltab[l];
            if (lptr.Address < 0)
            {
                await QuitAsync($"Undefined label: {lptr.Uninm}");
            }

            for (int rptr = lptr.Chain, next; rptr >= 0; rptr = next)
            {
                next = pspace[rptr].Lb1;
                pspace[rptr].Lb1 = lptr.Address;
            }
        }
    }

    // ycomp: y's table of each rune to the rune in the same place of the second string, as far as
    // the highest rune of the first; false when the two are not of a length or do not end.
    private bool Ycomp(SedCom r)
    {
        uint highc = 0;
        int tsp = cp;
        for (; L(tsp) != seof; tsp++)
        {
            tsp += L(tsp) == '\\' ? 1 : 0;
            if (L(tsp) is '\n' or 0)
            {
                return false;
            }

            highc = Math.Max(highc, L(tsp));
        }

        tsp++;
        var rp = new Dictionary<uint, uint>();
        int sp = cp;
        for (uint c; (c = L(sp++)) != seof;)
        {
            if (c == '\\' && L(sp) == 'n')
            {
                sp++;
                c = '\n';
            }

            uint t = L(tsp++);
            if (t == '\\' && L(tsp) == 'n')
            {
                t = '\n';
                tsp++;
            }

            rp[c] = t;

            // Stryker disable once Logical : a short or unended second string fails the check of its end just after
            if (t == seof || t == 0)
            {
                return false;
            }
        }

        if (L(tsp) != seof)
        {
            return false;
        }

        cp = tsp + 1;
        r.Ytab = (highc, rp);
        return true;
    }

    private async Task ExecuteAsync()
    {
        while ((spend = await GlineAsync(0)) >= 0)
        {
            for (int ipc = 0; pspace[ipc].Command != Cmd.None;)
            {
                if (!await ExecutableAsync(pspace[ipc]))
                {
                    ipc++;
                    continue;
                }

                await CommandAsync(pspace[ipc]);
                if (delflag)
                {
                    break;
                }

                if (jflag)
                {
                    jflag = false;

                    // Stryker disable once Block : a branch is resolved to a command or the end, never -1
                    if ((ipc = pspace[ipc].Lb1) < 0)
                    {
                        // Stryker disable once Statement : a branch is resolved to a command or the end, never -1
                        break;
                    }
                }
                else
                {
                    ipc++;
                }
            }

            if (!nflag && !delflag)
            {
                await PutlineAsync(fout, linebuf, spend);
            }

            // Stryker disable once Equality : arout of nothing writes nothing
            if (abuf.Count > 0)
            {
                await AroutAsync();
            }

            delflag = false;
        }
    }

    // executable: whether the command applies to this line, its range begun by a match of its
    // first address and running to one of its second.
    private async Task<bool> ExecutableAsync(SedCom ipc)
    {
        if (ipc.Active != 0)
        {
            // Stryker disable once Conditional : Active is 1 or 2 here
            ipc.Active = ipc.Active == 1 ? 2 : ipc.Active;
            switch (ipc.Ad2.Type)
            {
                case At.None:
                    ipc.Active = 0;
                    break;
                case At.Dol:
                    return !ipc.Negfl;
                case At.Line:
                    if (lnum <= ipc.Ad2.Line)
                    {
                        // Stryker disable once Conditional : a range by line ended here ends on the next line all the same
                        ipc.Active = ipc.Ad2.Line == lnum ? 0 : ipc.Active;
                        return !ipc.Negfl;
                    }

                    ipc.Active = 0;
                    return ipc.Negfl;
                default:
                    if (Match(ipc.Ad2.Rp, 0))
                    {
                        ipc.Active = 0;
                    }

                    return !ipc.Negfl;
            }
        }

        switch (ipc.Ad1.Type)
        {
            case At.None:
                return !ipc.Negfl;
            case At.Dol when dolflag:
                return !ipc.Negfl;
            case At.Line when ipc.Ad1.Line == lnum:
            case At.Re when Match(ipc.Ad1.Rp, 0):
                ipc.Active = 1;
                return !ipc.Negfl;
        }

        // Stryker disable once Statement : it awaits nothing
        await Task.CompletedTask;
        return ipc.Negfl;
    }

    // match: the expression from buf in the line, ^ only at its start; loc1 and loc2 the match.
    private bool Match(Regexp? pattern, int buf)
    {
        // Stryker disable once Block : an expression to match is always compiled
        if (pattern is null)
        {
            // Stryker disable once Boolean : an expression to match is always compiled
            return false;
        }

        subexp[0] = new Regexp.Resub(buf, -1);
        if (pattern.Rregexec(linebuf, subexp, Maxsub) > 0)
        {
            (loc1, loc2) = (subexp[0].Sp, subexp[0].Ep);
            return true;
        }

        // Stryker disable once Unary : a failed match's place is never read
        (loc1, loc2) = (-1, -1);
        return false;
    }

    private async Task<bool> SubstituteAsync(SedCom ipc)
    {
        if (!Match(ipc.Re1, 0))
        {
            return false;
        }

        // A global substitution steps past a match of nothing, so as not to match it again.
        sflag = true;
        if (!ipc.Gfl)
        {
            await DosubAsync(ipc.Rhs);
            return true;
        }

        do
        {
            int len = loc2 - loc1;
            await DosubAsync(ipc.Rhs);

            // Stryker disable once Block : the check a step on ends it at the line's end
            if (linebuf[loc2] == 0)
            {
                // Stryker disable once Statement : the check a step on ends it at the line's end
                break;
            }

            loc2 += len == 0 ? 1 : 0;
            if (linebuf[loc2] == 0)
            {
                break;
            }
        }
        while (Match(ipc.Re1, loc2));
        return true;
    }

    // dosub: the line with the match replaced by the right side, & the match and \1 to \9 its
    // subexpressions, built in genbuf and copied back; loc2 left after the replacement.
    private async Task DosubAsync(int rhsbuf)
    {
        int lp = 0, sp = 0, rp = rhsbuf;
        while (lp < loc1)
        {
            genbuf[sp++] = linebuf[lp++];
        }

        for (uint c; (c = addspace[rp++]) != 0;)
        {
            if (c == '&')
            {
                sp = await PlaceAsync(sp, loc1, loc2);
                continue;
            }

            if (c == Runemax && (c = addspace[rp++]) >= '1' && c < Maxsub + '0')
            {
                int n = (int)(c - '0');

                // Stryker disable once Logical : a subexpression's start and end are set together
                if (subexp[n].Sp < 0 || subexp[n].Ep < 0)
                {
                    await QuitAsync($"Invalid back reference \\{n}");
                }

                sp = await PlaceAsync(sp, subexp[n].Sp, subexp[n].Ep);
                continue;
            }

            genbuf[sp++] = c;
            if (sp >= Lbsize)
            {
                await QuitAsync("Output line too long");
            }
        }

        lp = loc2;
        loc2 = sp;
        do
        {
            genbuf[sp++] = linebuf[lp];
            if (linebuf[lp++] != 0 && sp >= Lbsize)
            {
                await QuitAsync("Output line too long");
            }
        }
        while (linebuf[lp - 1] != 0);

        lp = sp = 0;
        while ((linebuf[lp++] = genbuf[sp++]) != 0)
        {
        }

        spend = lp - 1;
    }

    private async Task<int> PlaceAsync(int sp, int l1, int l2)
    {
        while (l1 < l2)
        {
            genbuf[sp++] = linebuf[l1++];
            if (sp >= Lbsize)
            {
                await QuitAsync("Output line too long");
            }
        }

        return sp;
    }

    private async Task CommandAsync(SedCom ipc)
    {
        switch (ipc.Command)
        {
            case Cmd.A or Cmd.R:
                abuf.Add(Array.IndexOf(pspace, ipc));
                if (abuf.Count >= Maxadds)
                {
                    await QuitAsync($"Too many {(ipc.Command == Cmd.A ? "appends" : "reads")} after line {lnum}");
                }

                break;
            case Cmd.C:
                delflag = true;
                if (ipc.Active == 1)
                {
                    await PuttextAsync(ipc.Text);
                }

                break;
            case Cmd.D:
                delflag = true;
                break;
            case Cmd.CD:
                int nl = Array.IndexOf(linebuf, (uint)'\n', 0, spend);
                if (nl < 0)
                {
                    delflag = true;
                    return;
                }

                spend = Copy(linebuf, 0, linebuf, nl + 1);
                jflag = true;
                break;
            case Cmd.Eq:
                await fout.BwriteAsync(Encoding.UTF8.GetBytes(lnum.ToString(CultureInfo.InvariantCulture) + "\n"));
                break;
            case Cmd.G:
                spend = Copy(linebuf, 0, holdsp, 0);
                break;
            case Cmd.CG:
                linebuf[spend++] = '\n';
                spend = Append(linebuf, spend, holdsp, Lbsize);
                break;
            case Cmd.H:
                hspend = Copy(holdsp, 0, linebuf, 0);
                break;
            case Cmd.CH:
                holdsp[hspend++] = '\n';
                hspend = Append(holdsp, hspend, linebuf, Lbsize);
                break;
            case Cmd.I:
                await PuttextAsync(ipc.Text);
                break;
            case Cmd.B:
                jflag = true;
                break;
            case Cmd.L:
                await ListAsync();
                break;
            case Cmd.N:
                if (!nflag)
                {
                    await PutlineAsync(fout, linebuf, spend);
                }

                // Stryker disable once Equality : arout of nothing writes nothing
                if (abuf.Count > 0)
                {
                    await AroutAsync();
                }

                int execp = await GlineAsync(0);
                if (execp < 0)
                {
                    delflag = true;
                    break;
                }

                spend = execp;
                break;
            case Cmd.CN:

                // Stryker disable once Equality : arout of nothing writes nothing
                if (abuf.Count > 0)
                {
                    await AroutAsync();
                }

                linebuf[spend++] = '\n';
                int more = await GlineAsync(spend);

                // Stryker disable once Equality : the line joined holds at least its newline
                if (more < 0)
                {
                    delflag = true;
                    break;
                }

                spend = more;
                break;
            case Cmd.P:
                await PutlineAsync(fout, linebuf, spend);
                break;
            case Cmd.CP:
                await CpcomAsync();
                break;
            case Cmd.Q:
                if (!nflag)
                {
                    await PutlineAsync(fout, linebuf, spend);
                }

                // Stryker disable once Equality : arout of nothing writes nothing
                if (abuf.Count > 0)
                {
                    await AroutAsync();
                }

                await ExitsAsync(null);
                break;
            case Cmd.S:
                bool i = await SubstituteAsync(ipc);
                if (i && ipc.Pfl == 1)
                {
                    await PutlineAsync(fout, linebuf, spend);
                }
                else if (i && ipc.Pfl == 2)
                {
                    // P goes to cpcom, and on from there, past w.
                    await CpcomAsync();
                    break;
                }

                if (i && ipc.Fcode is { } w)
                {
                    await PutlineAsync(w, linebuf, spend);
                }

                break;
            case Cmd.T:
                if (sflag)
                {
                    (sflag, jflag) = (false, true);
                }

                break;
            case Cmd.W:
                await PutlineAsync(ipc.Fcode!, linebuf, spend);
                break;
            case Cmd.X:
                Copy(genbuf, 0, linebuf, 0);
                spend = Copy(linebuf, 0, holdsp, 0);
                hspend = Copy(holdsp, 0, genbuf, 0);
                break;
            default:
                (uint highc, Dictionary<uint, uint> table) = ipc.Ytab;
                for (int p1 = 0; linebuf[p1] != 0; p1++)
                {
                    if (linebuf[p1] <= highc)
                    {
                        linebuf[p1] = table.TryGetValue(linebuf[p1], out uint t) ? t : linebuf[p1];
                    }
                }

                break;
        }
    }

    // P: the line as far as its first newline.
    private async Task CpcomAsync()
    {
        for (int rp = 0; linebuf[rp] is not (0 or '\n'); rp++)
        {
            await fout.BputcAsync((byte)linebuf[rp]);
        }

        await fout.BputcAsync((byte)'\n');
    }

    // l: the line with what it cannot print as trans shows it, broken after each 72 bytes.
    private async Task ListAsync()
    {
        uint c = 0;
        int i = 0;
        for (int rp = 0; linebuf[rp] != 0; rp++)
        {
            c = linebuf[rp];
            string shown = c is >= 0x20 and < 0x7F and not '\\' ? ((char)c).ToString() : Trans(c);
            foreach (char u in shown)
            {
                c = u;
                await fout.BputcAsync((byte)c);
                if (i++ > 71)
                {
                    await fout.BwriteAsync("\\\n"u8.ToArray());
                    i = 0;
                }
            }
        }

        if (c == ' ')
        {
            await fout.BwriteAsync("\\n"u8.ToArray());
        }

        await fout.BputcAsync((byte)'\n');
    }

    private async Task PuttextAsync(int text)
    {
        for (int rp = text; addspace[rp] != 0; rp++)
        {
            await fout.BputruneAsync(addspace[rp]);
        }

        await fout.BputcAsync((byte)'\n');
    }

    // arout: the texts of the a commands and the files of the r ones, as they came.
    private async Task AroutAsync()
    {
        foreach (int a in abuf)
        {
            SedCom ipc = pspace[a];
            if (ipc.Command == Cmd.A)
            {
                await PuttextAsync(ipc.Text);
                continue;
            }

            // The name in 128 bytes, less a rune and its NUL.
            var buf = new List<byte>();
            byte[] b = new byte[Utf.UtfMax];
            for (int p1 = ipc.Text; addspace[p1] != 0 && buf.Count < 128 - Utf.UtfMax - 1; p1++)
            {
                buf.AddRange(b[..Utf.RuneToChar(b, addspace[p1])]);
            }

            int fd;
            try
            {
                fd = await process.OpenAsync(Encoding.UTF8.GetString([.. buf]), NinePConstants.OREAD);
            }
            catch (SyscallException)
            {
                continue;
            }

            var fi = new Biobuf(process, fd) { Errorf = SysfatalAsync, Iof = uflag ? FlushoutAsync : null };
            for (int c; (c = await fi.BgetcAsync()) >= 0;)
            {
                await fout.BputcAsync((byte)c);
            }

            await process.CloseAsync(fd);
        }

        abuf.Clear();
    }

    // gline: the next line of the input into linebuf from addr, without its newline or NULs, cut
    // at linebuf's end; with a $ address, whether it is the last is seen by reading on. The end
    // of the line, or -1 once the files are done.
    private async Task<int> GlineAsync(int addr)
    {
        if (f is null && !await OpendataAsync())
        {
            return -1;
        }

        sflag = false;
        lnum++;
        do
        {
            int p = addr;

            // Stryker disable once Conditional : a peeked NUL is dropped as a read one is
            for (long c = peekc != 0 ? peekc : await f!.BgetruneAsync(); c >= 0; c = await f!.BgetruneAsync())
            {
                if (c == '\n')
                {
                    if (dollars != 0 && (peekc = await f!.BgetruneAsync()) < 0 && fhead.Count == 0)
                    {
                        dolflag = true;
                    }

                    linebuf[p] = 0;
                    return p;
                }

                if (c != 0 && p < Lbsize)
                {
                    linebuf[p++] = (uint)c;
                }
            }

            // A last line without its newline.
            if (p != addr)
            {
                linebuf[p] = 0;
                peekc = -1;
                if (fhead.Count == 0)
                {
                    dolflag = true;
                }

                return p;
            }

            peekc = 0;

            // Stryker disable once Equality : standard input is never closed by sed's own descriptor
            if (ffd > 0)
            {
                await process.CloseAsync(ffd);
            }
        }
        while (await OpendataAsync());

        f = null;
        return -1;
    }

    private async Task<bool> OpendataAsync()
    {
        if (!fhead.TryDequeue(out string? name))
        {
            return false;
        }

        ffd = 0;
        if (name is not null)
        {
            try
            {
                ffd = await process.OpenAsync(name, NinePConstants.OREAD);
            }
            catch (SyscallException)
            {
                await QuitAsync($"Can't open {name}");
            }
        }

        f = new Biobuf(process, ffd) { Errorf = SysfatalAsync, Iof = uflag ? FlushoutAsync : null };
        return true;
    }

    // An address: a line, $, an expression, the last expression again, or none.
    private sealed class Addr
    {
        public At Type { get; set; }

        public int Line { get; set; }

        public Regexp? Rp { get; set; }
    }

    // A command: its addresses, its expression, text or table, where it branches to, the right side
    // of an s, the file it writes, and its flags; Active is 1 on the line that began its range, 2
    // after it.
    private sealed class SedCom
    {
        public Addr Ad1 { get; } = new();

        public Addr Ad2 { get; } = new();

        public Regexp? Re1 { get; set; }

        public int Text { get; set; }

        public (uint Highc, Dictionary<uint, uint> Table) Ytab { get; set; }

        public int Lb1 { get; set; } = -1;

        public int Rhs { get; set; }

        public Biobuf? Fcode { get; set; }

        public Cmd Command { get; set; }

        public bool Gfl { get; set; }

        public int Pfl { get; set; }

        public int Active { get; set; }

        public bool Negfl { get; set; }
    }

    // A label: its name, the branches to it before it is defined, and its command, or -1.
    private sealed class Label
    {
        // Stryker disable once String : a label's name is set before it is looked for
        public string Uninm { get; set; } = string.Empty;

        public int Chain { get; set; } = -1;

        // Stryker disable once Unary : a label's address is set before dechain reads it
        public int Address { get; set; } = -1;
    }
}
