using System.Text;

namespace NinePSharp.Fog.Rc;

// rc's lexer, lex.c, with here.c's reading of here documents. Characters are bytes. rc's addutf
// takes a UTF-8 sequence at once; its continuation bytes are word characters, so a word taking them
// one at a time reads the same.
internal sealed class RcLexer
{
    private const int EndOfFile = -1;
    private const int TokenSize = 8192;
    private static readonly Dictionary<string, int> Keywords = new(StringComparer.Ordinal)
    {
        ["for"] = RcToken.For, ["in"] = RcToken.In, ["while"] = RcToken.While, ["if"] = RcToken.If,
        ["not"] = RcToken.Not, ["~"] = RcToken.Twiddle, ["!"] = RcToken.Bang, ["@"] = RcToken.Subshell,
        ["switch"] = RcToken.Switch, ["fn"] = RcToken.Fn,
    };

    private readonly RcInput input;
    private readonly TextWriter errors;
    private readonly char[] token = new char[TokenSize];
    private readonly List<RcTree> hereDocuments = new();
    private string epilog = "}\n";
    private int epilogAt;
    private int peekc = '{';
    private int future = EndOfFile;
    private bool inQuote;
    private bool inComment;

    internal RcLexer(RcInput input, string file, TextWriter errors)
    {
        this.input = input;
        this.errors = errors;
        File = file;
    }

    internal string File { get; }

    internal int Line { get; set; } = 1;

    internal bool Eof { get; private set; }

    internal int LastC { get; private set; }

    internal bool LastWord { get; set; }

    internal bool LastDol { get; set; }

    internal bool DoPrompt { get; set; } = true;

    // Called where rc calls pprompt; null when there is no prompt.
    internal Action? Prompt { get; set; }

    internal int ErrorCount { get; set; }

    internal string? LastError { get; private set; }

    internal RcTree? Value { get; private set; }

    // Every token sets it before any error can name it.
    internal string? TokenText { get; private set; }

    // pfln
    internal static string Location(string? file, int line) =>
        file is null ? "rc" : line != 0 ? $"{file}:{line}" : file;

    internal static bool IdChar(int c) => c > ' ' && !"!\"#$%&'()+,-./:;<=>?@[\\]^`{|}~".Contains((char)c, StringComparison.Ordinal);

    // As `.` reads a file without -b: one command line at a time, not one braced block.
    internal void ReadLines()
    {
        peekc = EndOfFile;
        epilog = string.Empty;
    }

    internal RcTree NewTree(int type) => new(type, Line);

    internal RcTree Token(string text, int type) => new(type, Line) { Str = text };

    internal RcTree LookUp(string name)
    {
        RcTree tree = Token(name, RcToken.Word);
        if (Keywords.TryGetValue(name, out int type))
        {
            tree.Type = type;
        }

        return tree;
    }

    internal void SkipNewlines()
    {
        while (true)
        {
            SkipWhite();
            if (NextC() != '\n')
            {
                return;
            }

            Advance();
        }
    }

    internal int Lex()
    {
        int c = NextC();
        Value = null;

        // Embarrassing sneakiness, as rc says: after a word, '(' is a subscript and a word character
        // starts a concatenation.
        if (LastWord)
        {
            LastWord = false;
            if (c == '(')
            {
                Advance();
                TokenText = "( [SUB]";
                return RcToken.Sub;
            }

            // An inserted caret is never where a syntax error is found, so it leaves the token text.
            if (WordChar(c) || c is '\'' or '`' or '$' or '"')
            {
                return '^';
            }
        }

        inQuote = false;
        SkipWhite();

        // Only a word reads differently after $, as a variable name.
        bool afterDollar = LastDol;
        LastDol = (c = Advance()) == '$';
        if (Operator(c) is { } symbol)
        {
            return symbol;
        }

        if (!WordChar(c))
        {
            TokenText = ((char)c).ToString();
            return c;
        }

        return Word(c, afterDollar);
    }

    internal void Error(string message)
    {
        errors.Write(Location(File, Line));
        errors.Write(": ");
        if (TokenText is { Length: > 0 } text && text[0] != '\n')
        {
            errors.Write("token ");
            errors.Write(RcPrinter.Word(text));
            errors.Write(": ");
        }

        errors.Write(message);
        errors.Write('\n');
        LastWord = false;
        LastDol = false;
        while (LastC != '\n' && LastC != EndOfFile)
        {
            Advance();
        }

        ErrorCount++;
        LastError = message;
    }

    // heredoc: a here document's text is read after the line that names it.
    internal void HereDocument(RcTree redirection)
    {
        if (redirection.Child[0]!.Type != RcToken.Word)
        {
            Error("Bad here tag");
            return;
        }

        hereDocuments.Add(redirection);
    }

    // readhere
    internal void ReadHereDocuments()
    {
        foreach (RcTree redirection in hereDocuments)
        {
            redirection.Str = ReadHereDocument(redirection.Child[0]!);
        }

        hereDocuments.Clear();
    }

    private static bool WordChar(int c) => c != EndOfFile && !"\n \t#;&|^$=`'{}()<>".Contains((char)c, StringComparison.Ordinal);

    private int? Operator(int c)
    {
        switch (c)
        {
            case EndOfFile:
                TokenText = "EOF";
                return EndOfFile;
            case '$':
                if (NextIs('#'))
                {
                    TokenText = "$#";
                    return RcToken.Count;
                }

                if (NextIs('"'))
                {
                    TokenText = "$\"";
                    return '"';
                }

                TokenText = "$";
                return '$';
            case '&':
                if (NextIs('&'))
                {
                    SkipNewlines();
                    TokenText = "&&";
                    return RcToken.AndAnd;
                }

                TokenText = "&";
                return '&';
            case '|' when NextIs('|'):
                SkipNewlines();
                TokenText = "||";
                return RcToken.OrOr;
            case '|' or '<' or '>':
                return Redirection(c);
            case '\'':
                return Quoted();
            default:
                return null;
        }
    }

    private int Word(int c, bool afterDollar)
    {
        int w = 0;
        bool glob = false;
        while (true)
        {
            if (c is '*' or '[' or '?' or RcToken.Glob)
            {
                glob = true;
                w = AddToken(w, RcToken.Glob);
            }

            w = AddToken(w, c);
            c = NextC();
            if (afterDollar ? !IdChar(c) : !WordChar(c))
            {
                break;
            }

            Advance();
        }

        LastWord = true;
        TokenText = Text(w);
        RcTree tree = LookUp(TokenText);
        if (tree.Type != RcToken.Word)
        {
            LastWord = false;
        }
        else
        {
            tree.Glob = glob ? 1 : 0;
        }

        tree.Quoted = false;
        Value = tree;
        return tree.Type;
    }

    private int Redirection(int c)
    {
        RcTree tree = NewTree(0);
        int w = Redirector(c, tree);
        if (NextIs('[') && (w = Descriptors(tree, w)) == EndOfFile)
        {
            return EndOfFile;
        }

        TokenText = new string(token, 0, w);
        Value = tree;
        if (tree.Type == RcToken.Pipe)
        {
            SkipNewlines();
        }

        return tree.Type;
    }

    private int Redirector(int c, RcTree tree)
    {
        int w = 0;
        token[w++] = (char)c;
        switch (c)
        {
            case '|':
                tree.Type = RcToken.Pipe;
                tree.Fd0 = 1;
                tree.Fd1 = 0;
                break;
            case '>':
                tree.Type = RcToken.Redir;
                if (NextIs(c))
                {
                    tree.RType = RcToken.Append;
                    token[w++] = (char)c;
                }
                else
                {
                    tree.RType = RcToken.Write;
                }

                tree.Fd0 = 1;
                break;
            default:
                tree.Type = RcToken.Redir;
                if (NextIs(c))
                {
                    tree.RType = RcToken.Here;
                    token[w++] = (char)c;
                }
                else if (NextIs('>'))
                {
                    tree.RType = RcToken.ReadWrite;
                    token[w++] = (char)c;
                }
                else
                {
                    tree.RType = RcToken.Read;
                }

                tree.Fd0 = 0;
                break;
        }

        return w;
    }

    // The [n] or [n=m] or [n=] after a redirection or pipe.
    private int Descriptors(RcTree tree, int w)
    {
        token[w++] = '[';
        int c = Advance();
        token[w++] = (char)c;
        if (c is < '0' or > '9')
        {
            return RedirectionError(tree, w);
        }

        tree.Fd0 = Number(ref c, ref w);
        if (c == '=')
        {
            token[w++] = '=';
            if (tree.Type == RcToken.Redir)
            {
                tree.Type = RcToken.Dup;
            }

            c = Advance();
            if (c is >= '0' and <= '9')
            {
                tree.RType = RcToken.DupFd;
                tree.Fd1 = tree.Fd0;
                tree.Fd0 = Number(ref c, ref w);
            }
            else if (tree.Type == RcToken.Pipe)
            {
                return RedirectionError(tree, w);
            }
            else
            {
                tree.RType = RcToken.Close;
            }
        }

        if (c != ']' || (tree.Type == RcToken.Dup && tree.RType is RcToken.Here or RcToken.Append))
        {
            return RedirectionError(tree, w);
        }

        token[w++] = ']';
        return w;
    }

    private int Number(ref int c, ref int w)
    {
        int n = 0;
        do
        {
            n = (n * 10) + c - '0';
            token[w++] = (char)c;
            c = Advance();
        }
        while (c is >= '0' and <= '9');
        return n;
    }

    private int RedirectionError(RcTree tree, int w)
    {
        TokenText = new string(token, 0, w);
        Error(tree.Type == RcToken.Pipe ? "pipe syntax" : "redirection syntax");
        return EndOfFile;
    }

    private int Quoted()
    {
        int w = 0;
        LastWord = true;
        inQuote = true;
        while (true)
        {
            int c = Advance();
            if (c == EndOfFile)
            {
                break;
            }

            if (c == '\'')
            {
                if (NextC() != '\'')
                {
                    break;
                }

                Advance();
            }

            w = AddToken(w, c);
        }

        TokenText = Text(w);
        RcTree tree = Token(TokenText, RcToken.Word);
        tree.Quoted = true;
        Value = tree;
        return tree.Type;
    }

    // -1 is rc's null token pointer, after the buffer has overflowed.
    private int AddToken(int w, int value)
    {
        if (w < 0)
        {
            return w;
        }

        if (w == TokenSize - 1)
        {
            token[w] = '\0';
            TokenText = new string(token, 0, w);
            Error("token buffer too short");
            return -1;
        }

        token[w] = (char)value;
        return w + 1;
    }

    // After an overflow the token ends at the NUL AddToken put there, as in rc's buffer.
    private string Text(int w) => new(token, 0, w < 0 ? Array.IndexOf(token, '\0') : w);

    private int GetNext()
    {
        int c;
        if (peekc != EndOfFile)
        {
            c = peekc;
            peekc = EndOfFile;
            return c;
        }

        if (Eof)
        {
            return FromEpilog();
        }

        if (DoPrompt)
        {
            Prompt?.Invoke();
        }

        c = input.Read();
        if (c == '\\' && !inQuote)
        {
            c = input.Read();
            if (c == '\n' && !inComment)
            {
                Line++;
                DoPrompt = true;
                c = ' ';
            }
            else
            {
                peekc = c;
                c = '\\';
            }
        }

        if (c == EndOfFile)
        {
            Eof = true;
            return FromEpilog();
        }

        if (c == '\n')
        {
            DoPrompt = true;
        }

        return c;
    }

    private int FromEpilog()
    {
        if (epilogAt < epilog.Length)
        {
            return epilog[epilogAt++];
        }

        DoPrompt = true;
        return EndOfFile;
    }

    private int NextC()
    {
        if (future == EndOfFile)
        {
            future = GetNext();
        }

        return future;
    }

    private int Advance()
    {
        int c = NextC();
        LastC = future;
        future = EndOfFile;
        if (c == '\n')
        {
            Line++;
        }

        return c;
    }

    private void SkipWhite()
    {
        while (true)
        {
            int c = NextC();
            if (c == '#')
            {
                inComment = true;
                while (true)
                {
                    c = NextC();
                    if (c is '\n' or EndOfFile)
                    {
                        inComment = false;
                        break;
                    }

                    Advance();
                }
            }

            if (c is ' ' or '\t')
            {
                Advance();
            }
            else
            {
                return;
            }
        }
    }

    private bool NextIs(int c)
    {
        if (NextC() == c)
        {
            Advance();
            return true;
        }

        return false;
    }

    // readhere1: the text up to a line that is exactly the tag. rc reads into a char, so a 0xFF
    // byte ends the document as end of file does.
    private string? ReadHereDocument(RcTree tag)
    {
        Prompt?.Invoke();
        var text = new StringBuilder();
        string match = tag.Str!;
        int matched = 0;
        bool matching = true;
        int c;
        while ((c = input.Read()) != EndOfFile && c != 0xff)
        {
            if (c == 0)
            {
                Error("NUL bytes in here doc");
                return null;
            }

            if (c == '\n')
            {
                Line++;
                if (matching && matched == match.Length)
                {
                    text.Length -= matched;
                    break;
                }

                Prompt?.Invoke();
                matching = true;
                matched = 0;
            }
            else if (matching)
            {
                if (matched < match.Length && match[matched] == c)
                {
                    matched++;
                }
                else
                {
                    matching = false;
                }
            }

            text.Append((char)c);
        }

        DoPrompt = true;
        return text.ToString();
    }
}
