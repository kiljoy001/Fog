using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

public sealed class RcLexerTests
{
    // yyerror forgets that the last token was a word or $, and counts the error for compile.
    [Fact]
    public async Task AnErrorClearsTheWordStateAndIsCounted()
    {
        RcLexer lexer = Lexer("$a b\n");
        lexer.ReadLines();
        Assert.Equal('$', await lexer.LexAsync());
        Assert.Equal(RcToken.Word, await lexer.LexAsync());
        Assert.True(lexer.LastWord);
        lexer.LastDol = true;

        await lexer.ErrorAsync("broken");

        Assert.False(lexer.LastWord);
        Assert.False(lexer.LastDol);
        Assert.Equal(1, lexer.ErrorCount);
    }

    // rc's doprompt is shared by every input, so the end of one asks the input that resumes to prompt.
    [Fact]
    public async Task TheEndOfInputAsksForAPrompt()
    {
        RcLexer lexer = Lexer(string.Empty);
        lexer.ReadLines();
        lexer.DoPrompt = false;
        Assert.Equal(RcToken.EndOfFile, await lexer.LexAsync());
        Assert.True(lexer.DoPrompt);
    }

    private static RcLexer Lexer(string text) => new(RcInput.FromBytes(System.Text.Encoding.UTF8.GetBytes(text)), "/tmp/s", TextWriter.Null);
}
