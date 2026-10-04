using System.Text;

namespace NinePSharp.Fog.Rc;

// rc's lexer, lex.c, with here.c's reading of here documents. Characters are bytes. rc's addutf
// takes a UTF-8 sequence at once; its continuation bytes are word characters, so a word taking them
// one at a time reads the same. Reading may wait for input, so the lexer is asynchronous.
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

    private RcInput input;
    private TextWriter errors;
    private char[] token = new char[TokenSize];
    private List<RcTree> hereDocuments = new();
    private string epilog = "}\n";
    private int epilogAt;
    private int peekc = '{';
    private int future = EndOfFile;
    private bool inQuote;
    private bool inComment;

    // The length of the token being read; -1 is rc's null token pointer, after the buffer overflowed.
    private int length;

    internal RcLexer(RcInput input, string file, TextWriter errors)
    {
        this.input = input;
        this.errors = errors;
        File = file;
    }

    internal string File { get; }

    internal int Line { get; set; } = 1;

    internal bool Eof { get; set; }

    // Whether the last command compiled was an if, for `if not` on a later line.
    internal bool IfLast { get; set; }

    // Set by `. -q`: the commands it reads ignore rc -e.
    internal bool Quiet { get; set; }

    internal int LastC { get; private set; }

    internal bool LastWord { get; set; }

    internal bool LastDol { get; set; }

    internal bool DoPrompt { get; set; } = true;

    // Called where rc calls pprompt; null when there is no prompt.
    internal Func<ValueTask>? Prompt { get; set; }

    internal int ErrorCount { get; set; }

    // yyerror sets $status to the message.
    internal Action<string>? Failed { get; set; }

    internal string? LastError { get; private set; }

    internal RcTree? Value { get; private set; }

    // Every token sets it before any error can name it.
    internal string? TokenText { get; private set; }

    internal static string Location(string? file, int line) =>
        file is null ? "rc" : line != 0 ? $"{file}:{line}" : file;

    internal static bool IdChar(int c) => c > ' ' && !"!\"#$%&'()+,-./:;<=>?@[\\]^`{|}~".Contains((char)c, StringComparison.Ordinal);

    // pfln
    // A forked shell's copy, reading its own copy of the input and writing errors to its own io.
    internal RcLexer Copy(RcInput input, TextWriter errors)
    {
        var copy = (RcLexer)MemberwiseClone();
        copy.input = input;
        copy.errors = errors;
        copy.token = (char[])token.Clone();
        copy.hereDocuments = new(hereDocuments);
        return copy;
    }

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

    internal async ValueTask SkipNewlinesAsync()
    {
        while (true)
        {
            await SkipWhiteAsync();
            if (await NextCAsync() != '\n')
            {
                return;
            }

            await AdvanceAsync();
        }
    }

    internal async ValueTask<int> LexAsync()
    {
        int c = await NextCAsync();
        Value = null;

        // Embarrassing sneakiness, as rc says: after a word, '(' is a subscript and a word character
        // starts a concatenation.
        if (LastWord)
        {
            LastWord = false;
            if (c == '(')
            {
                await AdvanceAsync();
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
        await SkipWhiteAsync();

        // Only a word reads differently after $, as a variable name.
        bool afterDollar = LastDol;
        LastDol = (c = await AdvanceAsync()) == '$';
        if (await OperatorAsync(c) is { } symbol)
        {
            return symbol;
        }

        if (!WordChar(c))
        {
            TokenText = ((char)c).ToString();
            return c;
        }

        return await WordAsync(c, afterDollar);
    }

    internal async ValueTask ErrorAsync(string message)
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
            await AdvanceAsync();
        }

        ErrorCount++;
        LastError = message;
        Failed?.Invoke(message);
    }

    // heredoc: a here document's text is read after the line that names it.
    internal async ValueTask HereDocumentAsync(RcTree redirection)
    {
        if (redirection.Child[0]!.Type != RcToken.Word)
        {
            await ErrorAsync("Bad here tag");
            return;
        }

        hereDocuments.Add(redirection);
    }

    // readhere
    internal async ValueTask ReadHereDocumentsAsync()
    {
        foreach (RcTree redirection in hereDocuments)
        {
            redirection.Str = await ReadHereDocumentAsync(redirection.Child[0]!);
        }

        hereDocuments.Clear();
    }

    private static bool WordChar(int c) => c != EndOfFile && !"\n \t#;&|^$=`'{}()<>".Contains((char)c, StringComparison.Ordinal);

    private async ValueTask<int?> OperatorAsync(int c)
    {
        switch (c)
        {
            case EndOfFile:
                TokenText = "EOF";
                return EndOfFile;
            case '$':
                if (await NextIsAsync('#'))
                {
                    TokenText = "$#";
                    return RcToken.Count;
                }

                if (await NextIsAsync('"'))
                {
                    TokenText = "$\"";
                    return '"';
                }

                TokenText = "$";
                return '$';
            case '&':
                if (await NextIsAsync('&'))
                {
                    await SkipNewlinesAsync();
                    TokenText = "&&";
                    return RcToken.AndAnd;
                }

                TokenText = "&";
                return '&';
            case '|' when await NextIsAsync('|'):
                await SkipNewlinesAsync();
                TokenText = "||";
                return RcToken.OrOr;
            case '|' or '<' or '>':
                return await RedirectionAsync(c);
            case '\'':
                return await QuotedAsync();
            default:
                return null;
        }
    }

    private async ValueTask<int> WordAsync(int c, bool afterDollar)
    {
        length = 0;
        bool glob = false;
        while (true)
        {
            if (c is '*' or '[' or '?' or RcToken.Glob)
            {
                glob = true;
                await AddTokenAsync(RcToken.Glob);
            }

            await AddTokenAsync(c);
            c = await NextCAsync();
            if (afterDollar ? !IdChar(c) : !WordChar(c))
            {
                break;
            }

            await AdvanceAsync();
        }

        LastWord = true;
        TokenText = Text();
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

    private async ValueTask<int> RedirectionAsync(int c)
    {
        RcTree tree = NewTree(0);
        await RedirectorAsync(c, tree);
        if (await NextIsAsync('[') && !await DescriptorsAsync(tree))
        {
            return EndOfFile;
        }

        TokenText = new string(token, 0, length);
        Value = tree;
        if (tree.Type == RcToken.Pipe)
        {
            await SkipNewlinesAsync();
        }

        return tree.Type;
    }

    private async ValueTask RedirectorAsync(int c, RcTree tree)
    {
        length = 0;
        token[length++] = (char)c;
        switch (c)
        {
            case '|':
                tree.Type = RcToken.Pipe;
                tree.Fd0 = 1;
                tree.Fd1 = 0;
                break;
            case '>':
                tree.Type = RcToken.Redir;
                if (await NextIsAsync(c))
                {
                    tree.RType = RcToken.Append;
                    token[length++] = (char)c;
                }
                else
                {
                    tree.RType = RcToken.Write;
                }

                tree.Fd0 = 1;
                break;
            default:
                tree.Type = RcToken.Redir;
                if (await NextIsAsync(c))
                {
                    tree.RType = RcToken.Here;
                    token[length++] = (char)c;
                }
                else if (await NextIsAsync('>'))
                {
                    tree.RType = RcToken.ReadWrite;
                    token[length++] = (char)c;
                }
                else
                {
                    tree.RType = RcToken.Read;
                }

                tree.Fd0 = 0;
                break;
        }
    }

    // The [n] or [n=m] or [n=] after a redirection or pipe; false after a syntax error.
    private async ValueTask<bool> DescriptorsAsync(RcTree tree)
    {
        token[length++] = '[';
        int c = await AdvanceAsync();
        token[length++] = (char)c;
        if (c is < '0' or > '9')
        {
            return await RedirectionErrorAsync(tree);
        }

        (tree.Fd0, c) = await NumberAsync(c);
        if (c == '=')
        {
            token[length++] = '=';
            if (tree.Type == RcToken.Redir)
            {
                tree.Type = RcToken.Dup;
            }

            c = await AdvanceAsync();
            if (c is >= '0' and <= '9')
            {
                tree.RType = RcToken.DupFd;
                tree.Fd1 = tree.Fd0;
                (tree.Fd0, c) = await NumberAsync(c);
            }
            else if (tree.Type == RcToken.Pipe)
            {
                return await RedirectionErrorAsync(tree);
            }
            else
            {
                tree.RType = RcToken.Close;
            }
        }

        if (c != ']' || (tree.Type == RcToken.Dup && tree.RType is RcToken.Here or RcToken.Append))
        {
            return await RedirectionErrorAsync(tree);
        }

        token[length++] = ']';
        return true;
    }

    // The number starting with digit c, and the character after it.
    private async ValueTask<(int Value, int Next)> NumberAsync(int c)
    {
        int n = 0;
        do
        {
            n = (n * 10) + c - '0';
            token[length++] = (char)c;
            c = await AdvanceAsync();
        }
        while (c is >= '0' and <= '9');
        return (n, c);
    }

    private async ValueTask<bool> RedirectionErrorAsync(RcTree tree)
    {
        TokenText = new string(token, 0, length);
        await ErrorAsync(tree.Type == RcToken.Pipe ? "pipe syntax" : "redirection syntax");
        return false;
    }

    private async ValueTask<int> QuotedAsync()
    {
        length = 0;
        LastWord = true;
        inQuote = true;
        while (true)
        {
            int c = await AdvanceAsync();
            if (c == EndOfFile)
            {
                break;
            }

            if (c == '\'')
            {
                if (await NextCAsync() != '\'')
                {
                    break;
                }

                await AdvanceAsync();
            }

            await AddTokenAsync(c);
        }

        TokenText = Text();
        RcTree tree = Token(TokenText, RcToken.Word);
        tree.Quoted = true;
        Value = tree;
        return tree.Type;
    }

    private async ValueTask AddTokenAsync(int value)
    {
        if (length < 0)
        {
            return;
        }

        if (length == TokenSize - 1)
        {
            token[length] = '\0';
            TokenText = new string(token, 0, length);
            length = -1;
            await ErrorAsync("token buffer too short");
            return;
        }

        token[length++] = (char)value;
    }

    // After an overflow the token ends at the NUL AddToken put there, as in rc's buffer.
    private string Text() => new(token, 0, length < 0 ? Array.IndexOf(token, '\0') : length);

    private async ValueTask<int> GetNextAsync()
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

        if (DoPrompt && Prompt is not null)
        {
            await Prompt();
        }

        c = await input.ReadAsync();
        if (c == '\\' && !inQuote)
        {
            c = await input.ReadAsync();
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

    private async ValueTask<int> NextCAsync()
    {
        if (future == EndOfFile)
        {
            future = await GetNextAsync();
        }

        return future;
    }

    private async ValueTask<int> AdvanceAsync()
    {
        int c = await NextCAsync();
        LastC = future;
        future = EndOfFile;
        if (c == '\n')
        {
            Line++;
        }

        return c;
    }

    private async ValueTask SkipWhiteAsync()
    {
        while (true)
        {
            int c = await NextCAsync();
            if (c == '#')
            {
                inComment = true;
                while (true)
                {
                    c = await NextCAsync();
                    if (c is '\n' or EndOfFile)
                    {
                        inComment = false;
                        break;
                    }

                    await AdvanceAsync();
                }
            }

            if (c is ' ' or '\t')
            {
                await AdvanceAsync();
            }
            else
            {
                return;
            }
        }
    }

    private async ValueTask<bool> NextIsAsync(int c)
    {
        if (await NextCAsync() == c)
        {
            await AdvanceAsync();
            return true;
        }

        return false;
    }

    // readhere1: the text up to a line that is exactly the tag. rc reads into a char, so a 0xFF
    // byte ends the document as end of file does.
    private async ValueTask<string?> ReadHereDocumentAsync(RcTree tag)
    {
        if (Prompt is not null)
        {
            await Prompt();
        }

        var text = new StringBuilder();
        string match = tag.Str!;
        int matched = 0;
        bool matching = true;
        int c;
        while ((c = await input.ReadAsync()) != EndOfFile && c != 0xff)
        {
            if (c == 0)
            {
                await ErrorAsync("NUL bytes in here doc");
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

                if (Prompt is not null)
                {
                    await Prompt();
                }

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
