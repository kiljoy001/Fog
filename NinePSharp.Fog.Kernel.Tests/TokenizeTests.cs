using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class TokenizeTests
{
    [Theory]
    [InlineData("a b\tc\r\nd", new[] { "a", "b", "c", "d" })]
    [InlineData("  a  ", new[] { "a" })]
    [InlineData("'a b'", new[] { "a b" })]
    [InlineData("'it''s'", new[] { "it's" })]
    [InlineData("x'a b'y z", new[] { "xa by", "z" })]
    [InlineData("'abc", new[] { "abc" })]
    [InlineData("a'", new[] { "a" })]
    [InlineData("'a''", new[] { "a'" })]
    [InlineData("''", new[] { "" })]
    [InlineData("", new string[0])]
    public void TokenizeSplitsAndUnquotesAsLibcDoes(string line, string[] expected)
        => Assert.Equal(expected, Process.Tokenize(line, 10));

    [Fact]
    public void TokenizeStopsAtTheLimit() => Assert.Equal(["a", "b"], Process.Tokenize("a b c", 2));
}
