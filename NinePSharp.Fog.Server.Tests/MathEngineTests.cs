using System.Text;
using NinePSharp.Fog.Symbolics;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class MathEngineTests
{
    [Theory]
    [InlineData("evaluate", "1/2+1/3", null, "5/6")]
    [InlineData("evaluate", "1+2*2+3*2^2", null, "17")]
    [InlineData("simplify", "x+x", null, "2 * x")]
    [InlineData("differentiate", "1+2*x+3*x^2", "x", "2 + 6 * x")]
    [InlineData("evaluate", "(-2)/(-4)", null, "1/2")]
    public void ActualEngineVectors(string operation, string expression, string? variable, string expected)
    {
        var rows = MathEngine.ResultSchema.Parse(Execute(operation, expression, variable), 65536, 16);
        Assert.Equal("expression", Assert.Single(rows)["kind"]);
        Assert.Equal(expected, rows[0]["value"]);
    }

    [Theory]
    [InlineData("x^2-4", "-2,2")]
    [InlineData("(x-1)^2", "1")]
    [InlineData("x^2+1", "")]
    [InlineData("7", "")]
    [InlineData("x^2-2", "-sqrt(2),sqrt(2)")]
    [InlineData("x^4-1", "-1,1")]
    public void RootsAreRealDistinctAndOrdinal(string expression, string expected)
    {
        var rows = MathEngine.ResultSchema.Parse(Execute("solve", expression, "x"), 65536, 16);
        Assert.All(rows, row => Assert.Equal("root", row["kind"]));
        Assert.Equal(expected, string.Join(',', rows.Select(row => row["value"])));
    }

    [Theory]
    [InlineData("sin(x)")]
    [InlineData("2x")]
    [InlineData("x(x+1)")]
    [InlineData("x=2")]
    [InlineData("x;2")]
    [InlineData("[1,2]")]
    [InlineData("pi")]
    [InlineData("e")]
    [InlineData("i")]
    [InlineData("NaN")]
    [InlineData("x^-1")]
    [InlineData("x^x")]
    [InlineData("x^2^3")]
    [InlineData("1/x")]
    [InlineData("1/0")]
    [InlineData("x^1025")]
    [InlineData("x^99999999999999999999")]
    [InlineData("1.2")]
    [InlineData("\uFEFF1")]
    [InlineData("1\0")]
    [InlineData("")]
    [InlineData("(1")]
    [InlineData("1)")]
    [InlineData("+1")]
    public void UnsupportedLanguageIsRejected(string source) => Assert.Throws<FogException>(() => Execute("simplify", source));

    [Theory]
    [InlineData("evaluate", "x", null)]
    [InlineData("solve", "x+y", "x")]
    [InlineData("solve", "x^5-1", "x")]
    [InlineData("solve", "x", null)]
    [InlineData("evaluate", "1", "x")]
    [InlineData("differentiate", "x", "x+1")]
    [InlineData("integrate", "x", "x")]
    public void InapplicableOperationsAreRejected(string operation, string source, string? variable) =>
        Assert.Throws<FogException>(() => Execute(operation, source, variable));

    [Fact]
    public void ZeroPolynomialIsNotAnEmptyRootSet() => Assert.Equal("math-nonfinite",
        Assert.Throws<FogException>(() => Execute("solve", "x-x", "x")).Code);

    [Fact]
    public void AdmissionAndOutputAreBounded()
    {
        Assert.Throws<FogException>(() => Execute("evaluate", new string('9', 1235)));
        Assert.Throws<FogException>(() => Execute("evaluate", new string('(', 257) + "1" + new string(')', 257)));
        Assert.Throws<FogException>(() => Execute("simplify", new string('x', 65)));
        Assert.Throws<FogException>(() => MathEngine.Execute([0xff], "evaluate", null, 65536));
        Assert.Throws<FogException>(() => MathEngine.Execute(new byte[1048577], "evaluate", null, 65536));
        Assert.Throws<FogException>(() => MathEngine.Execute("1"u8.ToArray(), "evaluate", null, 10));
    }

    private static byte[] Execute(string operation, string source, string? variable = null) =>
        MathEngine.Execute(Encoding.UTF8.GetBytes(source), operation, variable, 65536);
}
