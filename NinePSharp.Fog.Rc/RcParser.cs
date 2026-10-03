namespace NinePSharp.Fog.Rc;

// yyparse for rc's grammar: yacc's table-driven LALR(1) driver over RcTables, with the actions of
// syn.y and the tree builders of tree.c. One call reads one command line.
internal sealed class RcParser(RcLexer lexer)
{
    private const int MaxDepth = 500;
    private const int NoSymbol = -1;
    private readonly int[] states = new int[MaxDepth];

    // Reducing an empty rule takes the value of the slot above the top, past the stack when it is full.
    private readonly RcTree?[] values = new RcTree?[MaxDepth + 1];

    internal enum Outcome
    {
        // A line was read and compile accepted it.
        Line,

        // End of input, or an error that rc does not recover from.
        Stop,
    }

    // yylex1: rc's token as yacc's symbol number. Characters yacc does not know are its unknown symbol.
    internal static int Symbol(int token)
    {
        short[] characters = RcTables.CharacterTokens;
        short[] privates = RcTables.PrivateTokens;
        int symbol = token == RcToken.EndOfFile ? characters[0]
            : token >= RcTables.Private ? privates[token - RcTables.Private]
            : token < characters.Length ? characters[token]
            : 0;
        return symbol == 0 ? privates[1] : symbol;
    }

    // yyexca: the action for a symbol in a state whose default is to look it up.
    internal static int Exception(int state, int symbol)
    {
        short[] table = RcTables.Exceptions;
        int i = 0;
        while (table[i] != -1 || table[i + 1] != state)
        {
            i += 2;
        }

        for (i += 2; table[i] != -2 && table[i] != symbol; i += 2)
        {
        }

        return table[i + 1];
    }

    // Reads the next line and hands it to compile, which returns whether it compiled. rc's grammar
    // has no error productions, so yacc's recovery pops every state and ends the parse at the first
    // error; the generator checks that no table index reaches RcTables.Last.
    internal Outcome Parse(Func<RcTree?, bool> compile)
    {
        int state = 0, symbol = NoSymbol, top = -1;
        RcTree? value = null, lexValue = null;
        while (true)
        {
            if (++top >= MaxDepth)
            {
                lexer.Error("yacc stack overflow");
                return Outcome.Stop;
            }

            states[top] = state;
            values[top] = value;

            int n = RcTables.StateActions[state];
            if (n > RcTables.Flag)
            {
                if (symbol == NoSymbol)
                {
                    symbol = Lex(out lexValue);
                }

                // A negative index is no shift; the generator checks index 0 is never one.
                n = RcTables.Actions[Math.Max(n + symbol, 0)];
                if (RcTables.Checks[n] == symbol)
                {
                    symbol = NoSymbol;
                    value = lexValue;
                    state = n;
                    continue;
                }
            }

            // Only state 0 looks its symbol up, and it has always read one to try a shift.
            n = RcTables.Defaults[state];
            if (n == -2)
            {
                n = Exception(state, symbol);
            }

            if (n == 0)
            {
                lexer.Error("syntax error");
                return Outcome.Stop;
            }

            // Reduce by production n; an empty rule takes the value above the top.
            int first = top - RcTables.RuleLengths[n] + 1;
            top = first - 1;
            value = values[first];
            int symbolOfRule = RcTables.RuleSymbols[n];
            int go = RcTables.Gotos[symbolOfRule];
            state = RcTables.Checks[state = RcTables.Actions[go + states[top] + 1]] == -symbolOfRule ? state : RcTables.Actions[go];
            switch (Reduce(n, first, ref value, compile))
            {
                case false:
                    return Outcome.Stop;
                case true:
                    return Outcome.Line;
            }
        }
    }

    private static RcTree Mung(RcTree t, RcTree? c0)
    {
        t.Child[0] = c0;
        return t;
    }

    private static RcTree Mung(RcTree t, RcTree? c0, RcTree? c1)
    {
        t.Child[0] = c0;
        t.Child[1] = c1;
        return t;
    }

    private static RcTree Mung(RcTree t, RcTree? c0, RcTree? c1, RcTree? c2)
    {
        t.Child[0] = c0;
        t.Child[1] = c1;
        t.Child[2] = c2;
        return t;
    }

    // A command's epilog of redirections ends with the command itself.
    private static RcTree? EpiMung(RcTree? command, RcTree? epilog)
    {
        if (epilog is null)
        {
            return command;
        }

        RcTree p = epilog;
        while (p.Child[1] is not null)
        {
            p = p.Child[1]!;
        }

        p.Child[1] = command;
        return epilog;
    }

    // A word list or concatenation holding a glob marks its globs as patterns.
    private static RcTree GlobProp(RcTree t)
    {
        RcTree? c0 = t.Child[0], c1 = t.Child[1];
        if (c1 is null)
        {
            while (c0 is { Type: RcToken.Words })
            {
                c1 = c0.Child[1];
                if (c1 is { Glob: not 0 })
                {
                    c1.Glob = 2;
                    t.Glob = 1;
                }

                c0 = c0.Child[0];
            }
        }
        else
        {
            if (c0!.Glob != 0)
            {
                c0.Glob = 2;
                t.Glob = 1;
            }

            if (c1.Glob != 0)
            {
                c1.Glob = 2;
                t.Glob = 1;
            }
        }

        return t;
    }

    private int Lex(out RcTree? value)
    {
        int symbol = Symbol(lexer.Lex());
        value = lexer.Value;
        return symbol;
    }

    // syn.y's actions, numbered as yacc numbers the productions. Rule 1 and rule 2 end the parse.
    private bool? Reduce(int rule, int first, ref RcTree? result, Func<RcTree?, bool> compile)
    {
        switch (rule)
        {
            case 1:
                return false;
            case 2:
                lexer.ReadHereDocuments();
                return compile(values[first]);
        }

        result = rule < 19 ? Line(rule, first, result)
            : rule < 39 ? Command(rule, first, result)
            : rule < 47 ? Simple(rule, first, result)
            : Word(rule, first, result);
        return null;
    }

    // line, body, cmdsa, brace, paren, assign, epilog, redir and the empty cmd.
    private RcTree? Line(int rule, int first, RcTree? result)
    {
        RcTree? At(int i) => values[first + i];
        switch (rule)
        {
            case 4 or 6:
                return Sequence(At(0), At(1));
            case 8:
                return Tree1('&', At(0));
            case 10:
                lexer.ReadHereDocuments();
                return result;
            case 11:
                return Tree1(RcToken.Brace, At(1));
            case 12:
                return Tree1(RcToken.Pcmd, At(1));
            case 13:
                return Tree2('=', At(0), At(2));
            case 14 or 18:
                return null;
            case 15:
                return Mung(At(0)!, At(0)!.Child[0], At(1));
            case 16:
                RcTree redirection = Mung(At(0)!, At(1));
                if (redirection.RType == RcToken.Here)
                {
                    lexer.HereDocument(redirection);
                }

                return redirection;
            default:
                return result;
        }
    }

    private RcTree? Command(int rule, int first, RcTree? result)
    {
        RcTree? At(int i) => values[first + i];
        switch (rule)
        {
            case 19:
                return EpiMung(At(0), At(1));
            case 20 or 22 or 24 or 26 or 28 or 30:
                lexer.SkipNewlines();
                return result;
            case 21 or 29:
                return Mung(At(0)!, At(1), At(3));
            case 23:
                return Mung(At(1)!, At(3));
            case 25:
                // for(i in) loops over nothing; for(i) over the arguments.
                return Mung(At(0)!, At(2), At(4) ?? Tree1(RcToken.Paren, null), At(7));
            case 27:
                return Mung(At(0)!, At(2), null, At(5));
            case 31:
                return Tree2(RcToken.Switch, At(1), At(3));
            case 32:
                return SimpleMung(At(0)!);
            case 33:
                return Mung(At(0)!, At(1), At(2));
            case 34:
                return Tree2(RcToken.AndAnd, At(0), At(2));
            case 35:
                return Tree2(RcToken.OrOr, At(0), At(2));
            case 36:
                return Mung(At(1)!, At(0), At(2));
            case 37:
                return Mung(At(0)!, At(0)!.Child[0], At(1));
            default:
                return Mung(At(0)!, At(0)!.Child[0], At(0)!.Child[1], At(1));
        }
    }

    // simple, and cmd's fn.
    private RcTree? Simple(int rule, int first, RcTree? result)
    {
        RcTree? At(int i) => values[first + i];
        switch (rule)
        {
            case 39 or 40:
                return Mung(At(0)!, At(1));
            case 41:
                return Tree2(RcToken.Fn, At(1), At(2));
            case 42:
                return Tree1(RcToken.Fn, At(1));
            case 44 or 45:
                return GlobProp(Tree2(RcToken.ArgList, At(0), At(1)));
            default:
                return result;
        }
    }

    // first, word, comword, keyword and words.
    private RcTree? Word(int rule, int first, RcTree? result)
    {
        RcTree? At(int i) => values[first + i];
        switch (rule)
        {
            case 47 or 50:
                return GlobProp(Tree2('^', At(0), At(2)));
            case 48:
                lexer.LastWord = true;
                At(0)!.Type = RcToken.Word;
                return result;
            case 51:
                return Tree1('$', At(1));
            case 52:
                return Tree2(RcToken.Sub, At(1), At(3));
            case 53:
                return Tree1('"', At(1));
            case 54:
                return Tree1(RcToken.Count, At(1));
            case 56:
                return Tree2('`', null, At(1));
            case 57:
                return Tree2('`', At(1), At(2));
            case 58:
                return GlobProp(Tree1(RcToken.Paren, At(1)));
            case 59:
                RcTree pipe = Mung(At(0)!, At(1));
                pipe.Type = RcToken.PipeFd;
                return pipe;
            case 70:
                return null;
            case 71:
                return Tree2(RcToken.Words, At(0), At(1));
            default:
                return result;
        }
    }

    private RcTree Tree1(int type, RcTree? c0) => Tree2(type, c0, null);

    // A sequence with an empty side is the other side.
    private RcTree? Sequence(RcTree? c0, RcTree? c1) => c0 is null ? c1 : c1 is null ? c0 : Tree2(';', c0, c1);

    // tree3's line is its first child's; rc's grammar builds none of these with a third child.
    private RcTree Tree2(int type, RcTree? c0, RcTree? c1)
    {
        RcTree t = lexer.NewTree(type);
        t.Child[0] = c0;
        t.Child[1] = c1;
        t.Line = (c0 ?? c1)?.Line ?? t.Line;
        return t;
    }

    // A simple command gets a SIMPLE root, and its redirections percolate above it.
    private RcTree SimpleMung(RcTree words)
    {
        RcTree t = Tree1(RcToken.Simple, words);
        t.Str = RcPrinter.Print(t);
        for (RcTree? u = t.Child[0]; u is { Type: RcToken.ArgList }; u = u.Child[0])
        {
            if (u.Child[1]!.Type is RcToken.Dup or RcToken.Redir)
            {
                u.Child[1]!.Child[1] = t;
                t = u.Child[1]!;
                u.Child[1] = null;
            }
        }

        return t;
    }
}
