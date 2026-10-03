using System.Text;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

public sealed class RcParserTests
{
    [Theory]
    [InlineData(null, 3, "rc")]
    [InlineData("/tmp/s", 0, "/tmp/s")]
    [InlineData("/tmp/s", 3, "/tmp/s:3")]
    public void LocationsAreWrittenAsPfln(string? file, int line, string expected) => Assert.Equal(expected, RcLexer.Location(file, line));

    // yylex1: end of file, yacc's private symbols and characters it does not know.
    [Fact]
    public void TokensMapToYaccSymbols()
    {
        Assert.Equal(RcTables.EndOfFileCode, RcParser.Symbol(RcToken.EndOfFile));
        Assert.Equal(RcTables.ErrorCode, RcParser.Symbol(RcTables.Private));
        int unknown = RcTables.PrivateTokens[1];
        Assert.Equal(unknown, RcParser.Symbol('a'));
        Assert.Equal(unknown, RcParser.Symbol(RcTables.CharacterTokens.Length));
        Assert.NotEqual(unknown, RcParser.Symbol('{'));
        Assert.NotEqual(unknown, RcParser.Symbol(RcToken.Count));
    }

    // State 0 reduces rc's empty input on end of file and an empty command otherwise; state 1 accepts.
    [Theory]
    [InlineData(0, RcTables.EndOfFileCode, 1)]
    [InlineData(0, 10, 18)]
    [InlineData(1, RcTables.EndOfFileCode, -1)]
    [InlineData(1, 10, 0)]
    public void ExceptionsAreLookedUpByStateAndSymbol(int state, int symbol, int action) =>
        Assert.Equal(action, RcParser.Exception(state, symbol));

    // As rc reads a string for eval or -c: the whole input is one braced block.
    [Fact]
    public void WithoutReadingLinesTheInputIsOneBlock()
    {
        var lexer = new RcLexer(RcInput.FromBytes("a\nb\n"u8.ToArray()), "eval", TextWriter.Null);
        var parser = new RcParser(lexer);
        var lines = new List<RcTree?>();
        Assert.Equal(RcParser.Outcome.Line, parser.Parse(tree => Keep(lines, tree)));
        Assert.Equal(RcParser.Outcome.Stop, parser.Parse(tree => Keep(lines, tree)));
        Assert.True(lexer.Eof);
        Assert.Equal("{\n\ta\n\tb\n}", RcPrinter.Print(Assert.Single(lines)));
    }

    // globprop: a word with a glob is a pattern inside a list, a concatenation or an argument list,
    // and marks that node as holding a glob. A lone word keeps the lexer's mark.
    [Fact]
    public void GlobsArePropagatedAsGlobpropDoes()
    {
        Assert.Equal(1, Line("*.c")!.Child[0]!.Glob);
        RcTree arguments = Line("*.c (a b*) x^*.y z")!.Child[0]!;
        Assert.Equal(2, FirstWord(arguments).Glob);
        RcTree concatenation = arguments.Child[0]!.Child[1]!;
        RcTree list = arguments.Child[0]!.Child[0]!.Child[1]!;
        Assert.Equal((2, 2, 0), (concatenation.Glob, concatenation.Child[1]!.Glob, concatenation.Child[0]!.Glob));
        Assert.Equal((2, 2, 0), (list.Glob, list.Child[0]!.Child[1]!.Glob, list.Child[0]!.Child[0]!.Child[1]!.Glob));
        Assert.Equal(0, arguments.Child[1]!.Glob);
        Assert.Equal(1, arguments.Glob);
    }

    private static bool Keep(List<RcTree?> lines, RcTree? tree)
    {
        lines.Add(tree);
        return true;
    }

    private static RcTree? Line(string text)
    {
        var lexer = new RcLexer(RcInput.FromBytes(Encoding.UTF8.GetBytes(text + "\n")), "/tmp/s", TextWriter.Null);
        lexer.ReadLines();
        RcTree? line = null;
        new RcParser(lexer).Parse(tree => (line = tree) is not null);
        return line;
    }

    private static RcTree FirstWord(RcTree arguments)
    {
        RcTree t = arguments;
        while (t.Type == RcToken.ArgList)
        {
            t = t.Child[0]!;
        }

        return t;
    }
}
