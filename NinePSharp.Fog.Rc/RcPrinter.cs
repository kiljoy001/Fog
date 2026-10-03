using System.Text;

namespace NinePSharp.Fog.Rc;

// Prints a parse tree as rc source, as pcmd.c does for whatis and for a simple command's text.
internal sealed class RcPrinter
{
    private static readonly Dictionary<int, string> Formats = new()
    {
        ['$'] = "$%t",
        ['"'] = "$\"%t",
        ['&'] = "%t&",
        ['^'] = "%t^%t",
        ['`'] = "`%t%t",
        [RcToken.AndAnd] = "%t && %t",
        [RcToken.Bang] = "! %t",
        [RcToken.Count] = "$#%t",
        [RcToken.Fn] = "fn %t %t",
        [RcToken.If] = "if%t%t",
        [RcToken.Not] = "if not %t",
        [RcToken.OrOr] = "%t || %t",
        [RcToken.Pcmd] = "(%t)",
        [RcToken.Paren] = "(%t)",
        [RcToken.Sub] = "$%t(%t)",
        [RcToken.Simple] = "%t",
        [RcToken.Subshell] = "@ %t",
        [RcToken.Switch] = "switch %t %t",
        [RcToken.Twiddle] = "~ %t %t",
        [RcToken.While] = "while %t%t",
    };

    private readonly StringBuilder output = new();
    private int tabs;

    // fnstr
    internal static string Print(RcTree? tree)
    {
        var printer = new RcPrinter();
        printer.Command(tree);
        return printer.output.ToString();
    }

    // pquo: in single quotes, with each quote doubled.
    internal static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    // pwrd: quoted if empty or if it holds a character rc gives meaning to. pwrd's test for a
    // negative char leaves bytes from 0x80 unquoted, as these are.
    internal static string Word(string text) =>
        text.Length == 0 || text.Any(c => c <= ' ' || "`^#*[]=|\\?${}()'<>&;".Contains(c))
            ? Quote(text)
            : text;

    // pdeglob: a Glob marker stands before the character it marks, which is printed.
    private static string Deglob(string text)
    {
        var plain = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == RcToken.Glob)
            {
                i++;
            }

            plain.Append(text[i]);
        }

        return plain.ToString();
    }

    private string Indent() => new('\t', tabs % 8);

    private void Command(RcTree? t)
    {
        if (t is null)
        {
            return;
        }

        RcTree? c0 = t.Child[0], c1 = t.Child[1], c2 = t.Child[2];
        if (Formats.TryGetValue(t.Type, out string? format))
        {
            Format(format, c0, c1);
            return;
        }

        switch (t.Type)
        {
            case RcToken.Brace:
                tabs++;
                Write("{\n" + Indent());
                Command(c0);
                tabs--;
                Write("\n" + Indent() + "}");
                break;
            case RcToken.ArgList:
                Format(c1 is null ? "%t" : "%t %t", c0, c1);
                break;
            case ';':
                Sequence(c0!, c1!);
                break;
            case RcToken.Words when c0 is null:
                Command(c1);
                break;
            case RcToken.Words:
                Format("%t %t", c0, c1);
                break;
            case RcToken.For when c1 is null:
                Format("for(%t)%t", c0, c2);
                break;
            case RcToken.For:
                Format("for(%t in %t)%t", c0, c1, c2);
                break;
            default:
                Leaf(t);
                break;
        }
    }

    private void Leaf(RcTree t)
    {
        RcTree? c0 = t.Child[0], c1 = t.Child[1], c2 = t.Child[2];
        switch (t.Type)
        {
            case RcToken.Word:
                Write(t.Quoted ? Quote(t.Str!) : Deglob(t.Str!));
                break;
            case RcToken.Dup:
                Write(t.RType == RcToken.DupFd ? $">[{t.Fd1}={t.Fd0}]" : $">[{t.Fd0}=]");
                Command(c1);
                break;
            case RcToken.PipeFd or RcToken.Redir:
                Redirection(t);
                break;
            case '=':
                Format(c2 is null ? "%t=%t" : "%t=%t %t", c0, c1, c2);
                break;
            default:
                Pipe(t);
                break;
        }
    }

    // pfmt with only %t, which prints the next tree.
    private void Format(string format, params RcTree?[] trees)
    {
        int next = 0;
        string[] parts = format.Split("%t");
        Write(parts[0]);
        foreach (string part in parts.Skip(1))
        {
            Command(trees[next++]);
            Write(part);
        }
    }

    private void Pipe(RcTree t)
    {
        Command(t.Child[0]);
        Write("|");
        if (t.Fd1 != 0)
        {
            Write($"[{t.Fd0}={t.Fd1}]");
        }
        else if (t.Fd0 != 1)
        {
            Write($"[{t.Fd0}]");
        }

        Command(t.Child[1]);
    }

    // The parser builds a sequence only of two commands.
    private void Sequence(RcTree c0, RcTree c1)
    {
        Command(c0);
        Write(c0.Line == c1.Line ? "; " : "\n" + Indent());
        Command(c1);
    }

    // A here document prints its tag, then its text and the tag on lines of their own.
    private void Redirection(RcTree t)
    {
        Write(" ");
        switch (t.RType)
        {
            case RcToken.Here:
                if (t.Child[1] is not null)
                {
                    Command(t.Child[1]);
                    Write(" ");
                }

                Write("<<");
                break;
            case RcToken.Read or RcToken.ReadWrite:
                Write(t.RType == RcToken.ReadWrite ? "<>" : "<");
                break;
            case RcToken.Append:
                Write(">>");
                break;
            case RcToken.Write:
                Write(">");
                break;
        }

        if (t.RType is RcToken.Here or RcToken.Read or RcToken.ReadWrite ? t.Fd0 != 0 : t.Fd0 != 1)
        {
            Write($"[{t.Fd0}]");
        }

        Command(t.Child[0]);
        if (t.RType == RcToken.Here)
        {
            Write("\n" + t.Str + t.Child[0]!.Str + "\n");
        }
        else if (t.Child[1] is not null)
        {
            Write(" ");
            Command(t.Child[1]);
        }
    }

    private void Write(string text) => output.Append(text);
}
