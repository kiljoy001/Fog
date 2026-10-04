namespace NinePSharp.Fog.Rc;

// code.c: compiles a parse tree to a code vector for the interpreter.
internal sealed class RcCompiler
{
    // The trees Words and Control compile; Processes compiles the rest.
    private static readonly HashSet<int> WordTypes =
    [
        '$', '"', RcToken.Sub, ';', '^', '`', RcToken.ArgList, RcToken.Words, RcToken.Pcmd, RcToken.Brace,
        RcToken.Paren, RcToken.Count, RcToken.Word,
    ];

    private static readonly HashSet<int> ControlTypes =
    [
        RcToken.AndAnd, RcToken.Bang, RcToken.Fn, RcToken.If, RcToken.Not, RcToken.OrOr, RcToken.Simple,
        RcToken.Switch, RcToken.Twiddle, RcToken.While, RcToken.For,
    ];

    private readonly List<RcInstruction> code = new();
    private readonly List<string> errors = new();
    private readonly RcLexer lex;
    private int codeline;

    private RcCompiler(RcLexer lex) => this.lex = lex;

    // compile: null when the line had errors, which yyerror has reported.
    public static async ValueTask<RcCode?> CompileAsync(RcLexer lex, RcTree? t, bool eflag)
    {
        var compiler = new RcCompiler(lex);
        compiler.code.Add(default);
        compiler.code.Add(new RcInstruction(RcOp.None, S: lex.File));
        compiler.Outcode(t, !lex.Quiet && eflag);
        foreach (string error in compiler.errors)
        {
            await lex.ErrorAsync(error);
        }

        if (lex.ErrorCount != 0)
        {
            return null;
        }

        compiler.Emitf(RcOp.Return);
        return new RcCode([.. compiler.code]);
    }

    // noglobs, where a plain string is wanted instead of a glob: remove the GLOB marks.
    private static void Unglob(RcTree? t) => Noglobs(t, word =>
    {
        word.Str = RcGlob.Deglob(word.Str!);
        word.Glob = 0;
    });

    // noglobs, where a pattern is wanted instead of a glob: mark it so Xglob is not compiled for it.
    private static void Pattern(RcTree? t) => Noglobs(t, word => word.Glob = 2);

    private static void Noglobs(RcTree? t, Action<RcTree> word)
    {
        if (t is null)
        {
            return;
        }

        if (t.Type == RcToken.Word && t.Glob != 0)
        {
            word(t);
        }

        if (t.Type is RcToken.Paren or RcToken.Words or '^')
        {
            t.Glob = 0;
            Noglobs(t.Child[1], word);
            Noglobs(t.Child[0], word);
        }
    }

    private static bool IsCase(RcTree t)
    {
        if (t.Type != RcToken.Simple)
        {
            return false;
        }

        do
        {
            t = t.Child[0]!;
        }
        while (t.Type == RcToken.ArgList);
        return t is { Type: RcToken.Word, Quoted: false, Str: "case" };
    }

    private int Emitf(RcOp op) => Emit(new RcInstruction(op));

    private int Emiti(int i) => Emit(new RcInstruction(RcOp.None, i));

    private int Emits(string s) => Emit(new RcInstruction(RcOp.None, S: s));

    private int Emit(RcInstruction instruction)
    {
        code.Add(instruction);
        return code.Count - 1;
    }

    // stuffdot: patch a jump address to here.
    private void Stuffdot(int a) => code[a] = code[a] with { I = code.Count };

    // The code a forked child runs, its lines counted afresh, ending in its exit.
    private void Outchild(RcTree? t, bool eflag)
    {
        codeline = 0;
        Outcode(t, eflag);
        Emitf(RcOp.Exit);
    }

    private void Outcode(RcTree? t, bool eflag)
    {
        if (t is null)
        {
            return;
        }

        RcTree? c0 = t.Child[0];
        if (t.Type is not (RcToken.Not or ';'))
        {
            lex.IfLast = false;
        }

        if (t.Line != codeline)
        {
            codeline = t.Line;
            if (code[^2].Op == RcOp.Srcline)
            {
                code[^1] = code[^1] with { I = codeline };
            }
            else
            {
                Emitf(RcOp.Srcline);
                Emiti(codeline);
            }
        }

        if (WordTypes.Contains(t.Type))
        {
            Words(t, eflag);
        }
        else if (ControlTypes.Contains(t.Type))
        {
            Control(t, eflag);
        }
        else
        {
            Processes(t, eflag);
        }

        if (t.Glob == 1)
        {
            Emitf(RcOp.Glob);
        }

        if (t.Type is not (RcToken.Not or ';'))
        {
            lex.IfLast = t.Type == RcToken.If;
        }
        else if (c0 is not null)
        {
            lex.IfLast = c0.Type == RcToken.If;
        }
    }

    // Words, substitutions and lists.
    private void Words(RcTree t, bool eflag)
    {
        RcTree? c0 = t.Child[0], c1 = t.Child[1];
        int p;
        switch (t.Type)
        {
            case '$':
                Emitf(RcOp.Mark);
                Unglob(c0);
                Outcode(c0, eflag);
                Emitf(RcOp.Dol);
                break;
            case '"':
                Emitf(RcOp.Mark);
                Emitf(RcOp.Mark);
                Unglob(c0);
                Outcode(c0, eflag);
                Emitf(RcOp.Dol);
                Emitf(RcOp.Qw);
                Emitf(RcOp.Push);
                break;
            case RcToken.Sub:
                Emitf(RcOp.Mark);
                Unglob(c0);
                Outcode(c0, eflag);
                Emitf(RcOp.Mark);
                Unglob(c1);
                Outcode(c1, eflag);
                Emitf(RcOp.Sub);
                break;
            case ';':
                Outcode(c0, eflag);
                Outcode(c1, eflag);
                break;
            case '^':
                Emitf(RcOp.Mark);
                Outcode(c1, eflag);
                Emitf(RcOp.Mark);
                Outcode(c0, eflag);
                Emitf(RcOp.Conc);
                break;
            case '`':
                Emitf(RcOp.Mark);
                if (c0 is not null)
                {
                    // The split is words, which compile alike whatever eflag is.
                    Unglob(c0);
                    Outcode(c0, eflag);
                }
                else
                {
                    Emitf(RcOp.Mark);
                    Emitf(RcOp.Word);
                    Emits("ifs");
                    Emitf(RcOp.Dol);
                }

                Emitf(RcOp.Qw);
                Emitf(RcOp.Backq);
                p = Emiti(0);
                Outchild(c1, false);
                Stuffdot(p);
                break;
            case RcToken.ArgList or RcToken.Words:
                Outcode(c1, eflag);
                Outcode(c0, eflag);
                break;
            case RcToken.Pcmd or RcToken.Brace or RcToken.Paren:
                Outcode(c0, eflag);
                break;
            case RcToken.Count:
                Emitf(RcOp.Mark);
                Unglob(c0);
                Outcode(c0, eflag);
                Emitf(RcOp.Count);
                break;
            case RcToken.Word:
                Emitf(RcOp.Word);
                Emits(t.Str!);
                break;
        }
    }

    // Commands and the ways they are combined.
    private void Control(RcTree t, bool eflag)
    {
        RcTree? c0 = t.Child[0], c1 = t.Child[1], c2 = t.Child[2];
        int p, q;
        switch (t.Type)
        {
            case RcToken.AndAnd:
                Outcode(c0, false);
                Emitf(RcOp.True);
                p = Emiti(0);
                Outcode(c1, eflag);
                Stuffdot(p);
                break;
            case RcToken.Bang:
                Outcode(c0, eflag);
                Emitf(RcOp.Bang);
                break;
            case RcToken.Fn:
                Emitf(RcOp.Mark);
                Unglob(c0);
                Outcode(c0, eflag);
                if (c1 is not null)
                {
                    Emitf(RcOp.Fn);
                    p = Emiti(0);
                    Emits(RcPrinter.Print(c1));
                    codeline = 0;
                    Outcode(c1, eflag);
                    Emitf(RcOp.Return);
                    Stuffdot(p);
                }
                else
                {
                    Emitf(RcOp.Delfn);
                }

                break;
            case RcToken.If:
                Outcode(c0, false);
                Emitf(RcOp.If);
                p = Emiti(0);
                Outcode(c1, eflag);
                Emitf(RcOp.Wastrue);
                Stuffdot(p);
                break;
            case RcToken.Not:
                if (!lex.IfLast)
                {
                    errors.Add("`if not' does not follow `if(...)'");
                }

                Emitf(RcOp.Ifnot);
                p = Emiti(0);
                Outcode(c0, eflag);
                Stuffdot(p);
                break;
            case RcToken.OrOr:
                Outcode(c0, false);
                Emitf(RcOp.False);
                p = Emiti(0);
                Outcode(c1, eflag);
                Stuffdot(p);
                break;
            case RcToken.Simple:
                Emitf(RcOp.Mark);
                Outcode(c0, eflag);
                Emitf(RcOp.Simple);
                Eflag(eflag);
                break;
            case RcToken.Switch:
                Codeswitch(t, eflag);
                break;
            case RcToken.Twiddle:
                Emitf(RcOp.Mark);
                Pattern(c1);
                Outcode(c1, eflag);
                Emitf(RcOp.Mark);
                Outcode(c0, eflag);
                Emitf(RcOp.Qw);
                Emitf(RcOp.Match);
                Eflag(eflag);
                break;
            case RcToken.While:
                q = code.Count;
                Outcode(c0, false);
                if (q == code.Count)
                {
                    // An empty condition is while(true).
                    Emitf(RcOp.Settrue);
                }

                Emitf(RcOp.True);
                p = Emiti(0);
                Outcode(c1, eflag);
                Emitf(RcOp.Jump);
                Emiti(q);
                Stuffdot(p);
                break;
            case RcToken.For:
                Emitf(RcOp.Mark);
                if (c1 is not null)
                {
                    Outcode(c1, eflag);
                }
                else
                {
                    Emitf(RcOp.Mark);
                    Emitf(RcOp.Word);
                    Emits("*");
                    Emitf(RcOp.Dol);
                }

                // A dummy value for Xlocal.
                Emitf(RcOp.Mark);
                Emitf(RcOp.Mark);
                Unglob(c0);
                Outcode(c0, eflag);
                Emitf(RcOp.Local);
                p = Emitf(RcOp.For);
                q = Emiti(0);
                Outcode(c2, eflag);
                Emitf(RcOp.Jump);
                Emiti(p);
                Stuffdot(q);
                Emitf(RcOp.Unlocal);
                break;
        }
    }

    // What forks, redirects or assigns.
    private void Processes(RcTree t, bool eflag)
    {
        RcTree? c0 = t.Child[0], c1 = t.Child[1];
        int p, q;
        switch (t.Type)
        {
            case '&':
                Emitf(RcOp.Async);
                p = Emiti(0);

                // Input from /dev/null. rc also calls rfork s here, for a new note group, which means
                // nothing until the kernel has notes.
                Emitf(RcOp.Mark);
                Emitf(RcOp.Word);
                Emits("/dev/null");
                Emitf(RcOp.Read);
                Emiti(0);

                Outchild(c0, eflag);
                Stuffdot(p);
                break;
            case RcToken.Subshell:
                Emitf(RcOp.Subshell);
                p = Emiti(0);
                Outchild(c0, eflag);
                Stuffdot(p);
                Eflag(eflag);
                break;
            case RcToken.Dup:
                if (t.RType == RcToken.DupFd)
                {
                    Emitf(RcOp.Dup);
                    Emiti(t.Fd0);
                    Emiti(t.Fd1);
                }
                else
                {
                    Emitf(RcOp.Close);
                    Emiti(t.Fd0);
                }

                Outcode(c1, eflag);
                Emitf(RcOp.Popredir);
                break;
            case RcToken.PipeFd:
                Emitf(RcOp.Pipefd);
                Emiti(t.RType);
                p = Emiti(0);
                Outchild(c0, eflag);
                Stuffdot(p);
                break;
            case RcToken.Redir:
                Redirection(t, eflag);
                break;
            case '=':
                Assignment(t, eflag);
                break;
            case RcToken.Pipe:
                Emitf(RcOp.Pipe);
                Emiti(t.Fd0);
                Emiti(t.Fd1);
                p = Emiti(0);
                q = Emiti(0);
                Outchild(c0, eflag);
                Stuffdot(p);
                codeline = 0;
                Outcode(c1, eflag);
                Emitf(RcOp.Return);
                Stuffdot(q);
                Emitf(RcOp.Pipewait);
                break;
        }
    }

    private void Eflag(bool eflag)
    {
        if (eflag)
        {
            Emitf(RcOp.Eflag);
        }
    }

    private void Redirection(RcTree t, bool eflag)
    {
        if (t.RType != RcToken.Here)
        {
            Emitf(RcOp.Mark);
            Outcode(t.Child[0], eflag);
        }

        switch (t.RType)
        {
            case RcToken.Append:
                Emitf(RcOp.Append);
                break;
            case RcToken.Write:
                Emitf(RcOp.Write);
                break;
            case RcToken.Read:
                Emitf(RcOp.Read);
                break;
            case RcToken.ReadWrite:
                Emitf(RcOp.Rdwr);
                break;
            default:
                Emitf(t.Child[0]!.Quoted ? RcOp.Hereq : RcOp.Here);
                Emits(t.Str!);
                break;
        }

        Emiti(t.Fd0);
        Outcode(t.Child[1], eflag);
        Emitf(RcOp.Popredir);
    }

    // var=value cmd sets locals for cmd; var=value alone assigns.
    private void Assignment(RcTree assignment, bool eflag)
    {
        RcTree? t = assignment;
        while (t is { Type: '=' })
        {
            t = t.Child[2];
        }

        if (t is not null)
        {
            for (t = assignment; t.Type == '='; t = t.Child[2]!)
            {
                Emitf(RcOp.Mark);
                Outcode(t.Child[1], eflag);
                Emitf(RcOp.Mark);
                Unglob(t.Child[0]);
                Outcode(t.Child[0], eflag);
                Emitf(RcOp.Local);
            }

            Outcode(t, eflag);
            for (t = assignment; t.Type == '='; t = t.Child[2]!)
            {
                Emitf(RcOp.Unlocal);
            }
        }
        else
        {
            for (t = assignment; t is not null; t = t.Child[2])
            {
                Emitf(RcOp.Mark);
                Outcode(t.Child[1], eflag);
                Emitf(RcOp.Mark);
                Unglob(t.Child[0]);
                Outcode(t.Child[0], eflag);
                Emitf(RcOp.Assign);
            }
        }
    }

    // switch code looks like this:
    //      Xmark
    //      (get switch value)
    //      Xjump   1f
    // out: Xjump   leave
    // 1:   Xmark
    //      (get case values)
    //      Xcase   1f
    //      (commands)
    //      Xjump   out
    // 1:   ...
    // leave:
    //      Xpopm
    private void Codeswitch(RcTree t, bool eflag)
    {
        RcTree? body = t.Child[1]!.Child[0];
        if (body is not { Type: ';' } || !IsCase(body.Child[0]!))
        {
            errors.Add("case missing in switch");
        }
        else
        {
            Cases(t.Child[0], body, eflag);
        }
    }

    private void Cases(RcTree? value, RcTree body, bool eflag)
    {
        RcTree t;
        Emitf(RcOp.Mark);
        Outcode(value, eflag);
        Emitf(RcOp.Qw);
        Emitf(RcOp.Jump);
        int nextcase = Emiti(0);
        int @out = Emitf(RcOp.Jump);
        int leave = Emiti(0);
        Stuffdot(nextcase);
        t = body;
        while (t.Type == ';')
        {
            RcTree tt = t.Child[1]!;
            Emitf(RcOp.Mark);

            // rc marks these words as patterns again; globprop already has, for every argument.
            for (t = t.Child[0]!.Child[0]!; t.Type == RcToken.ArgList; t = t.Child[0]!)
            {
                Outcode(t.Child[1], eflag);
            }

            Emitf(RcOp.Case);
            nextcase = Emiti(0);
            t = tt;
            while (true)
            {
                if (t.Type == ';')
                {
                    if (IsCase(t.Child[0]!))
                    {
                        break;
                    }

                    Outcode(t.Child[0], eflag);
                    t = t.Child[1]!;
                }
                else
                {
                    if (!IsCase(t))
                    {
                        Outcode(t, eflag);
                    }

                    break;
                }
            }

            Emitf(RcOp.Jump);
            Emiti(@out);
            Stuffdot(nextcase);
        }

        Stuffdot(leave);
        Emitf(RcOp.Popm);
    }
}
