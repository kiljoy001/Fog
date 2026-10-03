using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class ErrorsTests
{
    private static readonly string A = new('a', 20);
    private static readonly string B = new('b', 20);

    [Fact]
    public void AnErrorBeforeAnyElementIsNotNamed()
        => Assert.Equal("cannot exec directory", Errors.Name("/", 0, "cannot exec directory"));

    [Fact]
    public void AShortPathIsShownWhole()
        => Assert.Equal("file does not exist: '/tmp/missing'", Errors.Name("/tmp/missing/file", 2, "file does not exist"));

    [Fact]
    public void ALongPathShowsTheLongestShortSuffix()
    {
        string c = new('c', 21);
        string path = $"/{A}a/{B}b/{c}";
        Assert.Equal($"file does not exist: '.../{B}b/{c}'", Errors.Name(path, 3, "file does not exist"));
    }

    [Fact]
    public void APathOfAThirdOfErrMaxIsShortenedWhenTheErrorIsLong()
    {
        string error = new('e', 43);
        Assert.Equal($"{error}: '.../{B}'", Errors.Name($"/{A}/{B}", 2, error));
    }

    [Fact]
    public void AShortSuffixIsKeptWhenTheErrorAloneFillsTheMessage()
    {
        string error = new('e', 70);
        Assert.Equal($"{error}: '.../{B}'", Errors.Name($"/{A}/{B}", 2, error));
    }

    [Fact]
    public void ALongRelativeNameIsChopped()
    {
        string x = new('x', 70);
        Assert.Equal($"file does not exist: '...{x[..32]}'", Errors.Name(x, 1, "file does not exist"));
    }

    [Fact]
    public void ALongLastElementIsChopped()
    {
        string x = new('x', 70);
        Assert.Equal($"file does not exist: '...{x[..32]}'", Errors.Name("/" + x, 1, "file does not exist"));
    }

    [Theory]
    [InlineData(51, 126)]
    [InlineData(52, 127)]
    [InlineData(60, 127)]
    public void TheMessageIsCutToErrMax(int quotes, int length)
    {
        string full = $"file does not exist: '/{new string('\'', quotes * 2)}'";
        Assert.Equal(full[..length], Errors.Name("/" + new string('\'', quotes), 1, "file does not exist"));
    }
}
