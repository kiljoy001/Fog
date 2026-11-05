using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using static NinePSharp.Fog.Commands.AwkTables;

namespace NinePSharp.Fog.Commands;

// awk's lexer, lex.c, and yacc's yaccpar run on awkgram.y's tables, its actions those of the
// grammar, with parse.c's making of the tree.
public sealed partial class Awk
{
    private const int Firsttoken = 57346;
    private const int Program = 57347;
    private const int Pastat = 57348;
    private const int Pastat2 = 57349;
    private const int Xbegin = 57350;
    private const int Xend = 57351;
    private const int Nl = 57352;
    private const int Array = 57353;
    private const int Match = 57354;
    private const int Notmatch = 57355;
    private const int Matchop = 57356;
    private const int And = 57367;
    private const int Bor = 57368;
    private const int Append = 57369;
    private const int Eq = 57370;
    private const int Ge = 57371;
    private const int Gt = 57372;
    private const int Le = 57373;
    private const int Lt = 57374;
    private const int Ne = 57375;
    private const int In = 57376;
    private const int Arg = 57377;
    private const int Bltin = 57378;
    private const int Break = 57379;
    private const int Close = 57380;
    private const int Continue = 57381;
    private const int Delete = 57382;
    private const int Do = 57383;
    private const int Exit = 57384;
    private const int For = 57385;
    private const int Func = 57386;
    private const int Sub = 57387;
    private const int Gsub = 57388;
    private const int If = 57389;
    private const int Index = 57390;
    private const int Matchfcn = 57392;
    private const int Next = 57393;
    private const int Nextfile = 57394;
    private const int Add = 57395;
    private const int Minus = 57396;
    private const int Mult = 57397;
    private const int Divide = 57398;
    private const int Mod = 57399;
    private const int Assign = 57400;
    private const int Asgnop = 57401;
    private const int Addeq = 57402;
    private const int Subeq = 57403;
    private const int Multeq = 57404;
    private const int Diveq = 57405;
    private const int Modeq = 57406;
    private const int Poweq = 57407;
    private const int Print = 57408;
    private const int Printf = 57409;
    private const int Sprintf = 57410;
    private const int Else = 57411;
    private const int Intest = 57412;
    private const int Condexpr = 57413;
    private const int Postincr = 57414;
    private const int Preincr = 57415;
    private const int Postdecr = 57416;
    private const int Predecr = 57417;
    private const int Var = 57418;
    private const int Ivar = 57419;
    private const int Varnf = 57420;
    private const int Call = 57421;
    private const int Number = 57422;
    private const int String = 57423;
    private const int Regexpr = 57424;
    private const int Getline = 57425;
    private const int Return = 57426;
    private const int Split = 57427;
    private const int Substr = 57428;
    private const int While = 57429;
    private const int Cat = 57430;
    private const int Not = 57431;
    private const int Uminus = 57432;
    private const int Power = 57433;
    private const int Decr = 57434;
    private const int Incr = 57435;
    private const int Indirect = 57436;
    private const int Lasttoken = 57437;
    private const int Yymaxdepth = 150;

    // keep sorted: binary searched.
    private static readonly (string Word, int Sub, int Type)[] Keywords =
    [
        ("BEGIN", Xbegin, Xbegin), ("END", Xend, Xend), ("NF", Varnf, Varnf), ("atan2", Fatan, Bltin),
        ("break", Break, Break), ("close", Close, Close), ("continue", Continue, Continue), ("cos", Fcos, Bltin),
        ("delete", Delete, Delete), ("do", Do, Do), ("else", Else, Else), ("exit", Exit, Exit), ("exp", Fexp, Bltin),
        ("fflush", Fflush, Bltin), ("for", For, For), ("func", Func, Func), ("function", Func, Func),
        ("getline", Getline, Getline), ("gsub", Gsub, Gsub), ("if", If, If), ("in", In, In), ("index", Index, Index),
        ("int", Fint, Bltin), ("length", Flength, Bltin), ("log", Flog, Bltin), ("match", Matchfcn, Matchfcn),
        ("next", Next, Next), ("nextfile", Nextfile, Nextfile), ("print", Print, Print), ("printf", Printf, Printf),
        ("rand", Frand, Bltin), ("return", Return, Return), ("sin", Fsin, Bltin), ("split", Split, Split),
        ("sprintf", Sprintf, Sprintf), ("sqrt", Fsqrt, Bltin), ("srand", Fsrand, Bltin), ("sub", Sub, Sub),
        ("substr", Substr, Substr), ("system", Fsystem, Bltin), ("tolower", Ftolower, Bltin), ("toupper", Ftoupper, Bltin),
        ("utf", Futf, Bltin), ("while", While, While),
    ];

    // The operators of productions 134 to 139.
    private static readonly int[] Arith = [Add, Minus, Mult, Divide, Mod, Power];

    private readonly List<string> pfile = [];
    private readonly sbyte[] ebuf = new sbyte[300];
    private readonly sbyte[] yysbuf = new sbyte[100];
    private readonly List<char> tok = [];
    private readonly Yystype[] yyv = new Yystype[Yymaxdepth];
    private readonly int[] yys = new int[Yymaxdepth];
    private string? lexprog;
    private int lexpos;
    private int curpfile;
    private Bio? yyin;
    private int lineno = 1;
    private int bracecnt;
    private int brackcnt;
    private int parencnt;
    private bool sc;
    private bool reg;
    private int ep;
    private int yysptr;
    private int infunc;
    private int inloop;
    private string? curfname;
    private Node? arglist;
    private Node? beginloc;
    private Node? endloc;
    private Node? winner;
    private int paircnt;
    private int syntaxBeenHere;
    private int eprintBeenHere;
    private int bracecheckBeenHere;
    private int dbg;
    private Yystype yylval;
    private Yystype yyval;
    private int yychar;

    // cursource: the -f file being read; past the last, pfile's next entry, which is nil.
    private string? Cursource() => curpfile < pfile.Count ? pfile[curpfile] : null;

    // pgetc: a byte of the program files, each opened in turn, - standard input.
    private async Task<int> PgetcAsync()
    {
        for (; ;)
        {
            if (yyin is null)
            {
                if (curpfile >= pfile.Count)
                {
                    return Biobuf.Beof;
                }

                if (pfile[curpfile] == "-")
                {
                    yyin = stdin;
                }
                else if ((yyin = await BopenAsync(pfile[curpfile], NinePConstants.OREAD)) is null)
                {
                    await FatalAsync($"can't open file {pfile[curpfile]}");
                }

                lineno = 1;
            }

            int c = await yyin!.B.BgetcAsync();
            if (c != Biobuf.Beof)
            {
                return c;
            }

            // Stryker disable once Equality,Statement : ending standard input's buffer flushes nothing, and a file left open is not read again
            if (yyin != stdin)
            {
                await yyin.TermAsync();
            }

            yyin = null;
            curpfile++;
        }
    }

    // input: the next byte of the program, as a signed char, 0 at its end, kept in ebuf for
    // eprint.
    private async Task<int> InputAsync()
    {
        int c;
        if (yysptr > 0)
        {
            c = yysbuf[--yysptr];
        }
        else if (lexprog is not null)
        {
            c = lexpos < lexprog.Length ? lexprog[lexpos] : 0;

            // Stryker disable once Conditional : past the program's end it still reads 0
            lexpos += c != 0 ? 1 : 0;
        }
        else
        {
            c = await PgetcAsync();
        }

        if (c == '\n')
        {
            lineno++;
        }
        else if (c == Biobuf.Beof)
        {
            c = 0;
        }

        ep = ep >= ebuf.Length ? 0 : ep;
        return ebuf[ep++] = unchecked((sbyte)c);
    }

    private async Task UnputAsync(int c)
    {
        if (c == '\n')
        {
            lineno--;
        }

        if (yysptr >= yysbuf.Length)
        {
            await FatalAsync($"pushed back too much: {Lat(yysbuf.Take(20).Select(b => (byte)b).ToArray())}...");
        }

        yysbuf[yysptr++] = unchecked((sbyte)c);
        if (--ep < 0)
        {
            ep = ebuf.Length - 1;
        }
    }

    private async Task UnputstrAsync(string s)
    {
        for (int i = s.Length - 1; i >= 0; i--)
        {
            await UnputAsync(s[i]);
        }
    }

    private async Task<int> PeekAsync()
    {
        int c = await InputAsync();
        await UnputAsync(c);
        return c;
    }

    // gettok: a name, given as 'a', a number, given as '0', or the byte. Its buffer is kept from
    // token to token, as yylex's is: at the program's end it still holds the token before, and a
    // name the end cut short is not ended in it, so a longer one before shows past it.
    private async Task<(int C, string Buf, double F)> GettokAsync()
    {
        int c = await InputAsync();
        if (c == 0)
        {
            return (0, Tokstr(), 0);
        }

        Tokput(0, (char)(byte)c);
        Tokput(1, '\0');
        if (!Isalnum(c) && c != '.' && c != '_')
        {
            return (c, Tokstr(), 0);
        }

        int bp = 1;
        if (Isalpha(c) || c == '_')
        {
            while ((c = await InputAsync()) != 0)
            {
                if (Isalnum(c) || c == '_')
                {
                    Tokput(bp++, (char)(byte)c);
                    continue;
                }

                Tokput(bp, '\0');
                await UnputAsync(c);
                break;
            }

            return ('a', Tokstr(), 0);
        }

        while ((c = await InputAsync()) != 0)
        {
            if (Isdigit(c) || c is 'e' or 'E' or '.' or '+' or '-')
            {
                Tokput(bp++, (char)c);
                continue;
            }

            await UnputAsync(c);
            break;
        }

        Tokput(bp, '\0');
        string buf = Tokstr();
        bool n = ToNumber(buf, out double f, out int rem);
        await UnputstrAsync(buf[rem..]);
        Tokput(rem, '\0');
        return (n ? '0' : buf[0], buf[..rem], f);
    }

    private void Tokput(int i, char c)
    {
        while (tok.Count <= i)
        {
            tok.Add('\0');
        }

        tok[i] = c;
    }

    // The buffer as far as its first NUL.
    private string Tokstr()
    {
        int n = tok.IndexOf('\0');
        return new string([.. n < 0 ? tok : tok.Take(n)]);
    }

    private async Task<int> YylexAsync()
    {
        if (sc)
        {
            sc = false;
            return '}';
        }

        if (reg)
        {
            reg = false;
            return await RegexprAsync();
        }

        for (; ;)
        {
            (int c, string buf, double f) = await GettokAsync();

            // Stryker disable once Block : the switch's default gives 0 back too
            if (c == 0)
            {
                return 0;
            }

            // Stryker disable once Logical,Equality : gettok gives 'a' only for a name, which starts with a letter or _
            if (c == 'a' && buf.Length > 0 && (Isalpha(buf[0]) || buf[0] == '_'))
            {
                return await WordAsync(buf);
            }

            // Stryker disable once Equality : gettok gives '0' only for a number it took a byte of
            if (c == '0' && buf.Length > 0 && (Isdigit(buf[0]) || buf[0] == '.'))
            {
                yylval.P = Setsymtab(buf, buf, f, Con | Num, symtab);
                return Number;
            }

            yylval.I = c;
            switch (c)
            {
                case '\n':
                    return Nl;
                case '\r' or ' ' or '\t':
                    break;
                case '#':
                    while ((c = await InputAsync()) is not ('\n' or 0))
                    {
                    }

                    await UnputAsync(c);
                    break;
                case ';':
                    return ';';
                case '\\':
                    if (await PeekAsync() == '\n')
                    {
                        await InputAsync();
                    }
                    else if (await PeekAsync() == '\r')
                    {
                        await InputAsync();
                        await InputAsync();
                        lineno++;
                    }
                    else
                    {
                        return c;
                    }

                    break;
                case '&':
                    return await PeekAsync() == '&' ? await Two(And) : '&';
                case '|':
                    return await PeekAsync() == '|' ? await Two(Bor) : '|';
                case '!':
                    return await PeekAsync() == '=' ? await Two(Ne, Ne)
                        : await PeekAsync() == '~' ? await Two(Matchop, Notmatch) : Not;
                case '~':
                    yylval.I = Match;
                    return Matchop;
                case '<':
                    return await PeekAsync() == '=' ? await Two(Le, Le) : Set(Lt, Lt);
                case '=':
                    return await PeekAsync() == '=' ? await Two(Eq, Eq) : Set(Asgnop, Assign);
                case '>':
                    return await PeekAsync() == '=' ? await Two(Ge, Ge)
                        : await PeekAsync() == '>' ? await Two(Append, Append) : Set(Gt, Gt);
                case '+':
                    return await PeekAsync() == '+' ? await Two(Incr, Incr)
                        : await PeekAsync() == '=' ? await Two(Asgnop, Addeq) : '+';
                case '-':
                    return await PeekAsync() == '-' ? await Two(Decr, Decr)
                        : await PeekAsync() == '=' ? await Two(Asgnop, Subeq) : '-';
                case '*':
                    if (await PeekAsync() == '=')
                    {
                        return await Two(Asgnop, Multeq);
                    }

                    if (await PeekAsync() == '*')
                    {
                        await InputAsync();
                        return await PeekAsync() == '=' ? await Two(Asgnop, Poweq) : Power;
                    }

                    return '*';
                case '/':
                    return '/';
                case '%':
                    return await PeekAsync() == '=' ? await Two(Asgnop, Modeq) : '%';
                case '^':
                    return await PeekAsync() == '=' ? await Two(Asgnop, Poweq) : Power;
                case '$':
                    // BUG: awkward, if not wrong.
                    (c, buf, _) = await GettokAsync();

                    // Stryker disable once Logical : $name as Ivar is $(name) as Indirect makes it
                    if (c is '(' or '[' || (infunc != 0 && Isarg(buf) >= 0))
                    {
                        await UnputstrAsync(buf);
                        return Indirect;
                    }

                    if (Isalpha(c))
                    {
                        if (buf == "NF")
                        {
                            await UnputstrAsync("(NF)");
                            return Indirect;
                        }

                        yylval.P = Setsymtab(buf, string.Empty, 0.0, Str | Num, symtab);
                        return Ivar;
                    }

                    await UnputstrAsync(buf);
                    return Indirect;
                case '}':
                    if (--bracecnt < 0)
                    {
                        await SyntaxAsync("extra }");
                    }

                    sc = true;
                    return ';';
                case ']':
                    if (--brackcnt < 0)
                    {
                        await SyntaxAsync("extra ]");
                    }

                    return ']';
                case ')':
                    if (--parencnt < 0)
                    {
                        await SyntaxAsync("extra )");
                    }

                    return ')';
                case '{':
                    bracecnt++;
                    return '{';
                case '[':
                    brackcnt++;
                    return '[';
                case '(':
                    parencnt++;
                    return '(';
                case '"':
                    return await StringAsync();
                default:
                    return c;
            }
        }
    }

    // A token of two bytes, the second taken, its value i.
    private async Task<int> Two(int token, int? i = null)
    {
        await InputAsync();
        yylval.I = i ?? yylval.I;
        return token;
    }

    private int Set(int token, int i)
    {
        yylval.I = i;
        return token;
    }

    // string: a quoted string's escapes undone; it ends at a NUL in it.
    private async Task<int> StringAsync()
    {
        var buf = new System.Text.StringBuilder();
        for (int c; (c = await InputAsync()) != '"';)
        {
            switch (c)
            {
                case '\n' or '\r' or 0:
                    await SyntaxAsync($"non-terminated string {Prec10(Cstr(buf.ToString()))}...");
                    lineno++;
                    return 0;
                case '\\':
                    c = await InputAsync();
                    switch (c)
                    {
                        case '"':
                            buf.Append('"');
                            break;
                        case 'n':
                            buf.Append('\n');
                            break;
                        case 't':
                            buf.Append('\t');
                            break;
                        case 'f':
                            buf.Append('\f');
                            break;
                        case 'r':
                            buf.Append('\r');
                            break;
                        case 'b':
                            buf.Append('\b');
                            break;
                        case 'v':
                            buf.Append('\v');
                            break;
                        case 'a':
                            buf.Append('\a');
                            break;
                        case '\\':
                            buf.Append('\\');
                            break;
                        case >= '0' and <= '7':
                            int n = c - '0';
                            if ((c = await PeekAsync()) is >= '0' and < '8')
                            {
                                n = (8 * n) + await InputAsync() - '0';
                                if ((c = await PeekAsync()) is >= '0' and < '8')
                                {
                                    n = (8 * n) + await InputAsync() - '0';
                                }
                            }

                            buf.Append((char)(byte)n);
                            break;
                        case 'x':
                            var xbuf = new System.Text.StringBuilder();
                            while ((c = await InputAsync()) != 0 && xbuf.Length < 100 - 2)
                            {
                                if (!Isxdigit(c))
                                {
                                    break;
                                }

                                xbuf.Append((char)c);
                            }

                            await UnputAsync(c);
                            buf.Append((char)(byte)Libc.Strtol(xbuf.ToString(), 16, out _));
                            break;
                        default:
                            buf.Append((char)(byte)c);
                            break;
                    }

                    break;
                default:
                    buf.Append((char)(byte)c);
                    break;
            }
        }

        string s = Cstr(buf.ToString());
        yylval.P = Setsymtab(s + " ", s, 0.0, Con | Str | Dontfree, symtab);
        return String;
    }

    // %.10s.
    private string Prec10(string s) => Lat(Libc.Sprint("%.10s"u8.ToArray(), [Libc.FmtArg.Of(Bytes(s))]));

    private async Task<int> WordAsync(string w)
    {
        int n = System.Array.BinarySearch(Keywords.Select(k => k.Word).ToArray(), w, StringComparer.Ordinal);
        if (n >= 0)
        {
            (_, int sub, int type) = Keywords[n];
            yylval.I = sub;

            // word's case FSYSTEM is never a keyword's type, system's being BLTIN, so -safe leaves
            // system as it is.
            switch (type)
            {
                case Func when infunc != 0:
                    await SyntaxAsync("illegal nested function");
                    return type;
                case Return when infunc == 0:
                    await SyntaxAsync("return not in function");
                    return type;
                case Varnf:
                    yylval.P = nfloc;
                    return Varnf;
                default:
                    return type;
            }
        }

        int c = await PeekAsync();
        if (c != '(' && infunc != 0 && (n = Isarg(w)) >= 0)
        {
            yylval.I = n;
            return Arg;
        }

        yylval.P = Setsymtab(w, string.Empty, 0.0, Str | Num | Dontfree, symtab);
        return c == '(' ? Call : Var;
    }

    // regexpr: the expression up to its closing /, which is given back.
    private async Task<int> RegexprAsync()
    {
        var buf = new System.Text.StringBuilder();
        for (int c; (c = await InputAsync()) is not ('/' or 0);)
        {
            if (c == '\n')
            {
                await SyntaxAsync($"newline in regular expression {Prec10(Cstr(buf.ToString()))}...");
                await UnputAsync('\n');
                break;
            }

            buf.Append((char)(byte)c);
            if (c == '\\')
            {
                buf.Append((char)(byte)await InputAsync());
            }
        }

        yylval.P = Cstr(buf.ToString());
        await UnputAsync('/');
        return Regexpr;
    }

    // yylex1: the token as yacc numbers it.
    private async Task<int> Yylex1Async()
    {
        int c = await YylexAsync();

        // Stryker disable once Conditional,Equality : yylex gives no byte past 127 nor 126, which is ~, and no token of Yyprivate or past the last
        int t = c <= 0 ? Yytok1[0]
            : c < Yytok1.Length ? Yytok1[c]
            : c >= Yyprivate && c < Yyprivate + Yytok2.Length ? Yytok2[c - Yyprivate]
            : 0;

        // Stryker disable once Conditional : 0 shifts in no state, so a token with no number is a syntax error either way
        return t == 0 ? Yytok2[1] : t;
    }

    // yaccpar.
    private async Task<int> YyparseAsync()
    {
        int yystate = 0, yyerrflag = 0, yyp = -1, yyn;
        yychar = -1;
        for (; ;)
        {
            // yystack: the state and its value pushed.
            if (++yyp >= Yymaxdepth)
            {
                await SyntaxAsync("yacc stack overflow");
                return 1;
            }

            yys[yyp] = yystate;
            yyv[yyp] = yyval;

        Yynewstate:
            yyn = Yypact[yystate];
            if (yyn > Yyflag)
            {
                // Stryker disable once Equality : Yylex1 gives a token of 1 or more
                if (yychar < 0)
                {
                    yychar = await Yylex1Async();
                }

                yyn += yychar;

                // Stryker disable once Equality : no state's offset and token sum to 0 or to Yylast but LASTTOKEN's, which yylex never gives
                if (yyn >= 0 && yyn < Yylast && Yychk[yyn = Yyact[yyn]] == yychar)
                {
                    // A valid shift.
                    yychar = -1;
                    yyval = yylval;
                    yystate = yyn;
                    yyerrflag -= yyerrflag > 0 ? 1 : 0;
                    continue;
                }
            }

            // yydefault.
            yyn = Yydef[yystate];
            if (yyn == -2)
            {
                // Stryker disable once Equality : Yylex1 gives a token of 1 or more
                if (yychar < 0)
                {
                    yychar = await Yylex1Async();
                }

                int x = 0;
                while (!(Yyexca[x] == -1 && Yyexca[x + 1] == yystate))
                {
                    x += 2;
                }

                // Stryker disable once Equality : no token in Yyexca is 0
                for (x += 2; Yyexca[x] >= 0 && Yyexca[x] != yychar; x += 2)
                {
                }

                if ((yyn = Yyexca[x + 1]) < 0)
                {
                    return 0;
                }
            }

            if (yyn == 0)
            {
                // An error: try to resume parsing.
                if (yyerrflag == 3)
                {
                    // No shift yet: the token is dropped.
                    if (yychar == Yyeofcode)
                    {
                        return 1;
                    }

                    yychar = -1;
                    goto Yynewstate;
                }

                if (yyerrflag == 0)
                {
                    await SyntaxAsync("syntax error");
                }

                yyerrflag = 3;
                for (; yyp >= 0; yyp--)
                {
                    yyn = Yypact[yys[yyp]] + Yyerrcode;

                    // Stryker disable once Equality : no state's offset and the error token sum to 0 or to Yylast
                    if (yyn >= 0 && yyn < Yylast && Yychk[yystate = Yyact[yyn]] == Yyerrcode)
                    {
                        break;
                    }
                }

                // Stryker disable once Block : state 0, at the stack's bottom, shifts the error token, as program: error has it
                if (yyp < 0)
                {
                    return 1;
                }

                continue;
            }

            // A reduction by production yyn.
            int yypt = yyp;
            yyp -= Yyr2[yyn];
            yyval = yyv[yyp + 1];
            int yym = yyn;
            yyn = Yyr1[yyn];
            int yyg = Yypgo[yyn];
            int yyj = yyg + yys[yyp] + 1;

            // Stryker disable once Equality : no goto's offset and state sum to Yylast
            if (yyj >= Yylast || Yychk[yystate = Yyact[yyj]] != -yyn)
            {
                yystate = Yyact[yyg];
            }

            await ActionAsync(yym, yypt);
        }
    }

    private Yystype V(int yypt, int k) => yyv[yypt - k];

    // yacc's recovery from a syntax error leaves in a value's place what was there, a variable's
    // cell among them, which C's union reads as a node; no program is run after one, so an empty
    // node stands in.
    private Node Np(int yypt, int k) => V(yypt, k).P is not (null or Node) ? new Node() : (Node)V(yypt, k).P!;

    private Node? NpN(int yypt, int k) => V(yypt, k).P is not (null or Node) ? new Node() : (Node?)V(yypt, k).P;

    // The grammar's actions.
    private async Task ActionAsync(int yym, int t)
    {
        switch (yym)
        {
            case 1:
                if (exitstatus is null)
                {
                    winner = Stat3(Program, beginloc, NpN(t, 0), endloc);
                }

                break;
            case 2:

                // Stryker disable once Unary : bracecheck reads the program to its end, so the next token is its end either way
                yychar = -1;
                await BracecheckAsync();
                await SyntaxAsync("bailing out");
                break;
            case 13 or 15 or 17 or 112 or 126:
                inloop++;
                break;
            case 14:
                --inloop;
                yyval.P = Stat4(For, NpN(t, 9), Notnull(Np(t, 6)), NpN(t, 3), NpN(t, 0));
                break;
            case 16:
                --inloop;
                yyval.P = Stat4(For, NpN(t, 7), null, NpN(t, 3), NpN(t, 0));
                break;
            case 18:
                --inloop;
                yyval.P = Stat3(In, NpN(t, 5), await MakearrAsync(Np(t, 3)), NpN(t, 0));
                break;
            case 19 or 20:
                await SetfnameAsync((Cell)V(t, 0).P!);
                break;
            case 21 or 184:
                yyval.P = Notnull(Np(t, 1));
                break;
            case 26 or 28:
                yyval.I = 0;
                break;
            case 30 or 32 or 128:
                yyval.P = null;
                break;
            case 33 or 84 or 120 or 158:
                yyval.P = NpN(t, 1);
                break;
            case 96:
                yyval.P = V(t, 1).P;
                break;
            case 34:
                yyval.P = Notnull(Np(t, 0));
                break;
            case 35:
                yyval.P = Stat2(Pastat, NpN(t, 0), Stat2(Print, Rectonode(), null));
                break;
            case 36:
                yyval.P = Stat2(Pastat, NpN(t, 3), NpN(t, 1));
                break;
            case 37:
                yyval.P = await Pa2statAsync(NpN(t, 2), NpN(t, 0), Stat2(Print, Rectonode(), null));
                break;
            case 38:
                yyval.P = await Pa2statAsync(NpN(t, 5), NpN(t, 3), NpN(t, 1));
                break;
            case 39:
                yyval.P = Stat2(Pastat, null, NpN(t, 1));
                break;
            case 40:
                beginloc = Linkum(beginloc, NpN(t, 1));
                yyval.P = null;
                break;
            case 41:
                endloc = Linkum(endloc, NpN(t, 1));
                yyval.P = null;
                break;
            case 42:
                infunc++;
                break;
            case 43:
                infunc--;
                curfname = null;
                await DefnAsync((Cell)V(t, 7).P!, NpN(t, 5), NpN(t, 1));
                yyval.P = null;
                break;
            case 45 or 47 or 78 or 79 or 81:
                yyval.P = Linkum(NpN(t, 2), NpN(t, 0));
                break;
            case 48 or 59 or 63 or 64 or 65 or 66 or 67 or 68:
                yyval.P = Op2(V(t, 1).I, NpN(t, 2), NpN(t, 0));
                break;
            case 49 or 60:
                yyval.P = Op3(Condexpr, Notnull(Np(t, 4)), NpN(t, 2), NpN(t, 0));
                break;
            case 50 or 61:
                yyval.P = Op2(Bor, Notnull(Np(t, 2)), Notnull(Np(t, 0)));
                break;
            case 51 or 62:
                yyval.P = Op2(And, Notnull(Np(t, 2)), Notnull(Np(t, 0)));
                break;
            case 52 or 69:
                yyval.P = Op3(V(t, 1).I, null, NpN(t, 2), await CompreAsync((string)V(t, 0).P!));
                break;
            case 53 or 70:
                yyval.P = Constnode(Np(t, 0))
                    ? Op3(V(t, 1).I, null, NpN(t, 2), await CompreAsync(Strnode(Np(t, 0))))
                    : Op3(V(t, 1).I, 1, NpN(t, 2), NpN(t, 0));
                break;
            case 54 or 71:
                yyval.P = Op2(Intest, NpN(t, 2), await MakearrAsync(Np(t, 0)));
                break;
            case 55 or 72:
                yyval.P = Op2(Intest, NpN(t, 3), await MakearrAsync(Np(t, 0)));
                break;
            case 56 or 75:
                yyval.P = Op2(Cat, NpN(t, 1), NpN(t, 0));
                break;
            case 73:
                if (safe)
                {
                    await SyntaxAsync("cmd | getline is unsafe");
                }
                else
                {
                    yyval.P = Op3(Getline, NpN(t, 0), V(t, 2).I, NpN(t, 3));
                }

                break;
            case 74:
                if (safe)
                {
                    await SyntaxAsync("cmd | getline is unsafe");
                }
                else
                {
                    yyval.P = Op3(Getline, null, V(t, 1).I, NpN(t, 2));
                }

                break;
            case 82:
                yyval.P = Rectonode();
                break;
            case 93:
                yyval.P = Op3(Match, null, Rectonode(), await CompreAsync((string)V(t, 0).P!));
                break;
            case 94:
                yyval.P = Op1(Not, Notnull(Np(t, 0)));
                break;
            case 95:
                reg = true;
                break;
            case 99 or 100 or 101:
                if (safe)
                {
                    await SyntaxAsync(yym == 99 ? "print | is unsafe" : yym == 100 ? "print >> is unsafe" : "print > is unsafe");
                }
                else
                {
                    yyval.P = Stat3(V(t, 3).I, NpN(t, 2), V(t, 1).I, NpN(t, 0));
                }

                break;
            case 102:
                yyval.P = Stat3(V(t, 1).I, NpN(t, 0), null, null);
                break;
            case 103:
                yyval.P = Stat2(Delete, await MakearrAsync(Np(t, 3)), NpN(t, 1));
                break;
            case 104:
                yyval.P = Stat2(Delete, await MakearrAsync(Np(t, 0)), null);
                break;
            case 105:
                yyval.P = Exptostat(Np(t, 0));
                break;
            case 106:
                yychar = -1;
                await SyntaxAsync("illegal statement");
                break;
            case 109:
                if (inloop == 0)
                {
                    await SyntaxAsync("break illegal outside of loops");
                }

                yyval.P = Stat1(Break, null);
                break;
            case 110:
                yyval.P = Stat1(Close, NpN(t, 1));
                break;
            case 111:
                if (inloop == 0)
                {
                    await SyntaxAsync("continue illegal outside of loops");
                }

                yyval.P = Stat1(Continue, null);
                break;
            case 113:
                --inloop;
                break;
            case 114:
                yyval.P = Stat2(Do, NpN(t, 6), Notnull(Np(t, 2)));
                break;
            case 115:
                yyval.P = Stat1(Exit, NpN(t, 1));
                break;
            case 116:
                yyval.P = Stat1(Exit, null);
                break;
            case 118:
                yyval.P = Stat3(If, NpN(t, 3), NpN(t, 2), NpN(t, 0));
                break;
            case 119:
                yyval.P = Stat3(If, NpN(t, 1), NpN(t, 0), null);
                break;
            case 121:
                if (infunc != 0)
                {
                    await SyntaxAsync("next is illegal inside a function");
                }

                yyval.P = Stat1(Next, null);
                break;
            case 122:
                if (infunc != 0)
                {
                    await SyntaxAsync("nextfile is illegal inside a function");
                }

                yyval.P = Stat1(Nextfile, null);
                break;
            case 123:
                yyval.P = Stat1(Return, NpN(t, 1));
                break;
            case 124:
                yyval.P = Stat1(Return, null);
                break;
            case 127:
                --inloop;
                yyval.P = Stat2(While, NpN(t, 2), NpN(t, 0));
                break;
            case 130:
                yyval.P = Linkum(NpN(t, 1), NpN(t, 0));
                break;
            case 133:
                yyval.P = Op2(Diveq, NpN(t, 3), NpN(t, 0));
                break;
            case >= 134 and <= 139:
                yyval.P = Op2(Arith[yym - 134], NpN(t, 2), NpN(t, 0));
                break;
            case 140:
                yyval.P = Op1(Uminus, NpN(t, 0));
                break;
            case 141:
                yyval.P = NpN(t, 0);
                break;
            case 142:
                yyval.P = Op1(Not, Notnull(Np(t, 0)));
                break;
            case 143:
                yyval.P = Op2(Bltin, V(t, 2).I, Rectonode());
                break;
            case 144:
                yyval.P = Op2(Bltin, V(t, 3).I, NpN(t, 1));
                break;
            case 145:
                yyval.P = Op2(Bltin, V(t, 0).I, Rectonode());
                break;
            case 146:
                yyval.P = Op2(Call, Celltonode((Cell)V(t, 2).P!, Cvar), null);
                break;
            case 147:
                yyval.P = Op2(Call, Celltonode((Cell)V(t, 3).P!, Cvar), NpN(t, 1));
                break;
            case 148:
                yyval.P = Op1(Predecr, NpN(t, 0));
                break;
            case 149:
                yyval.P = Op1(Preincr, NpN(t, 0));
                break;
            case 150:
                yyval.P = Op1(Postdecr, NpN(t, 1));
                break;
            case 151:
                yyval.P = Op1(Postincr, NpN(t, 1));
                break;
            case 152:
                yyval.P = Op3(Getline, NpN(t, 2), V(t, 1).I, NpN(t, 0));
                break;
            case 153:
                yyval.P = Op3(Getline, null, V(t, 1).I, NpN(t, 0));
                break;
            case 154:
                yyval.P = Op3(Getline, NpN(t, 0), null, null);
                break;
            case 155:
                yyval.P = Op3(Getline, null, null, null);
                break;
            case 156:
                yyval.P = Op2(Index, NpN(t, 3), NpN(t, 1));
                break;
            case 157:
                await SyntaxAsync("index() doesn't permit regular expressions");
                yyval.P = Op2(Index, NpN(t, 3), V(t, 1).P);
                break;
            case 159:
                yyval.P = Op3(Matchfcn, null, NpN(t, 3), await CompreAsync((string)V(t, 1).P!));
                break;
            case 160:

                // Stryker disable once Conditional : a constant's expression compiled when it is run is the one compiled here
                yyval.P = Constnode(Np(t, 1))
                    ? Op3(Matchfcn, null, NpN(t, 3), await CompreAsync(Strnode(Np(t, 1))))
                    : Op3(Matchfcn, 1, NpN(t, 3), NpN(t, 1));
                break;
            case 161 or 166:
                yyval.P = Celltonode((Cell)V(t, 0).P!, Ccon);
                break;
            case 162:
                yyval.P = Op4(Split, NpN(t, 5), await MakearrAsync(Np(t, 3)), NpN(t, 1), String);
                break;
            case 163:
                yyval.P = Op4(Split, NpN(t, 5), await MakearrAsync(Np(t, 3)), await CompreAsync((string)V(t, 1).P!), Regexpr);
                break;
            case 164:
                yyval.P = Op4(Split, NpN(t, 3), await MakearrAsync(Np(t, 1)), null, String);
                break;
            case 165:
                yyval.P = Op1(V(t, 3).I, NpN(t, 1));
                break;
            case 167:
                yyval.P = Op4(V(t, 5).I, null, await CompreAsync((string)V(t, 3).P!), NpN(t, 1), Rectonode());
                break;
            case 168:

                // Stryker disable once Conditional : a constant's expression compiled when it is run is the one compiled here
                yyval.P = Constnode(Np(t, 3))
                    ? Op4(V(t, 5).I, null, await CompreAsync(Strnode(Np(t, 3))), NpN(t, 1), Rectonode())
                    : Op4(V(t, 5).I, 1, NpN(t, 3), NpN(t, 1), Rectonode());
                break;
            case 169:
                yyval.P = Op4(V(t, 7).I, null, await CompreAsync((string)V(t, 5).P!), NpN(t, 3), NpN(t, 1));
                break;
            case 170:

                // Stryker disable once Conditional : a constant's expression compiled when it is run is the one compiled here
                yyval.P = Constnode(Np(t, 5))
                    ? Op4(V(t, 7).I, null, await CompreAsync(Strnode(Np(t, 5))), NpN(t, 3), NpN(t, 1))
                    : Op4(V(t, 7).I, 1, NpN(t, 5), NpN(t, 3), NpN(t, 1));
                break;
            case 171:
                yyval.P = Op3(Substr, NpN(t, 5), NpN(t, 3), NpN(t, 1));
                break;
            case 172:
                yyval.P = Op3(Substr, NpN(t, 3), NpN(t, 1), null);
                break;
            case 175:
                yyval.P = Op2(Array, await MakearrAsync(Np(t, 3)), NpN(t, 1));
                break;
            case 176:
                yyval.P = Op1(Indirect, Celltonode((Cell)V(t, 0).P!, Cvar));
                break;
            case 177:
                yyval.P = Op1(Indirect, NpN(t, 0));
                break;
            case 178:
                arglist = null;
                yyval.P = null;
                break;
            case 179:
                yyval.P = arglist = Celltonode((Cell)V(t, 0).P!, Cvar);
                break;
            case 180:
                await CheckdupAsync(NpN(t, 2), (Cell)V(t, 0).P!);
                yyval.P = arglist = Linkum(NpN(t, 2), Celltonode((Cell)V(t, 0).P!, Cvar));
                break;
            case 181:
                yyval.P = Celltonode((Cell)V(t, 0).P!, Cvar);
                break;
            case 182:
                yyval.P = Op1(Arg, V(t, 0).I);
                break;
            case 183:
                yyval.P = Op1(Varnf, V(t, 0).P);
                break;
        }
    }

    private async Task SetfnameAsync(Cell p)
    {
        if ((p.Tval & Arr) != 0)
        {
            await SyntaxAsync($"{p.Nval} is an array, not a function");
        }
        else if ((p.Tval & Fcn) != 0)
        {
            await SyntaxAsync($"you can't define function {p.Nval} more than once");
        }

        curfname = p.Nval;
    }

    private bool Constnode(Node p) => p.Ntype == Nvalue && ((Cell)p.Narg[0]!).Csub == Ccon;

    private string Strnode(Node p) => ((Cell)p.Narg[0]!).Sval;

    // Stryker disable once Conditional,Logical : a comparison's value is a number, and a number is unequal to the null cell's 0 as it is true
    private Node Notnull(Node n) => n.Nobj is Le or Lt or Eq or Ne or Gt or Ge or Bor or And or Not ? n : Op2(Ne, n, nullnode);

    private async Task CheckdupAsync(Node? vl, Cell cp)
    {
        for (; vl is not null; vl = vl.Nnext)
        {
            if (cp.Nval == ((Cell)vl.Narg[0]!).Nval)
            {
                await SyntaxAsync($"duplicate argument {cp.Nval}");

                // Stryker disable once Statement : the list holds a name once, as a second is refused here
                break;
            }
        }
    }

    // parse.c.
    private Node Nodealloc(int n) => new() { Lineno = lineno, Narg = new object?[n] };

    private Node Exptostat(Node a)
    {
        a.Ntype = Nstat;
        return a;
    }

    private Node NodeN(int ntype, int a, params object?[] args)
    {
        Node x = Nodealloc(args.Length);
        (x.Ntype, x.Nobj) = (ntype, a);
        args.CopyTo(x.Narg, 0);
        return x;
    }

    private Node Stat1(int a, object? b) => NodeN(Nstat, a, b);

    private Node Stat2(int a, object? b, object? c) => NodeN(Nstat, a, b, c);

    private Node Stat3(int a, object? b, object? c, object? d) => NodeN(Nstat, a, b, c, d);

    private Node Stat4(int a, object? b, object? c, object? d, object? e) => NodeN(Nstat, a, b, c, d, e);

    private Node Op1(int a, object? b) => NodeN(Nexpr, a, b);

    private Node Op2(int a, object? b, object? c) => NodeN(Nexpr, a, b, c);

    private Node Op3(int a, object? b, object? c, object? d) => NodeN(Nexpr, a, b, c, d);

    private Node Op4(int a, object? b, object? c, object? d, object? e) => NodeN(Nexpr, a, b, c, d, e);

    private Node Celltonode(Cell a, int b)
    {
        (a.Ctype, a.Csub) = (Ocell, b);
        return NodeN(Nvalue, 0, a);
    }

    private Node Rectonode() => Op1(Indirect, Celltonode(literal0, Cunk));

    private async Task<Node> MakearrAsync(Node p)
    {
        if (p.Ntype == Nvalue)
        {
            var cp = (Cell)p.Narg[0]!;
            if ((cp.Tval & Fcn) != 0)
            {
                await SyntaxAsync($"{cp.Nval} is a function, not an array");
            }
            else if ((cp.Tval & Arr) == 0)
            {
                // Stryker disable once String : an array's string is never read
                (cp.Arr, cp.Sval, cp.Tval) = (Makesymtab(Nsymtab), string.Empty, Arr);
            }
        }

        return p;
    }

    private async Task<Node> Pa2statAsync(Node? a, Node? b, Node? c)
    {
        Node x = NodeN(Nstat, Pastat2, a, b, c, paircnt);
        if (paircnt++ >= pairstack.Length)
        {
            await SyntaxAsync($"limited to {pairstack.Length} pat,pat statements");
        }

        return x;
    }

    private Node? Linkum(Node? a, Node? b)
    {
        // Stryker disable once Block : a program with an error is not run, however its statements are linked
        if (exitstatus is not null)
        {
            return a;
        }

        if (a is null)
        {
            return b;
        }

        // Stryker disable once Block : linking null after a's last statement leaves it as it is
        if (b is null)
        {
            return a;
        }

        Node c = a;
        while (c.Nnext is not null)
        {
            c = c.Nnext;
        }

        c.Nnext = b;
        return a;
    }

    private async Task DefnAsync(Cell v, Node? vl, Node? st)
    {
        // Stryker disable once Statement,String : setfname has refused an array's name and bailed out before this
        if ((v.Tval & Arr) != 0)
        {
            await SyntaxAsync($"`{v.Nval}' is an array name and a function name");
            return;
        }

        if (Isarg(v.Nval!) != -1)
        {
            await SyntaxAsync($"`{v.Nval}' is both function name and argument name");
            return;
        }

        (v.Tval, v.Body) = (Fcn, st);
        int n = 0;
        for (Node? p = vl; p is not null; p = p.Nnext)
        {
            n++;
        }

        v.Fval = n;
    }

    private int Isarg(string s)
    {
        int n = 0;
        for (Node? p = arglist; p is not null; p = p.Nnext, n++)
        {
            if (((Cell)p.Narg[0]!).Nval == s)
            {
                return n;
            }
        }

        return -1;
    }

    // YYSTYPE's union: p, cp and s share P; i is I.
    private struct Yystype
    {
        public object? P;
        public int I;
    }

    private sealed class Node
    {
        public int Ntype { get; set; }

        public Node? Nnext { get; set; }

        public int Lineno { get; set; }

        public int Nobj { get; set; }

        public object?[] Narg { get; set; } = [];
    }
}
