using NinePSharp.Fog.Client;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// fog's rconnect address, rc words and working directory: what it dials, how it quotes what it
// sends rc, and where the session starts.
public sealed class RcpuTests
{
    // A bare host takes the service given, host!port its port, and net!host!port a network of tcp
    // or net; rcpu and ticket are named ports, any other a number.
    [Theory]
    [InlineData("fog", "rcpu", "fog", 17019)]
    [InlineData("fog", "ticket", "fog", 567)]
    [InlineData("fog!ticket", "rcpu", "fog", 567)]
    [InlineData("fog!564", "rcpu", "fog", 564)]
    [InlineData("tcp!fog!rcpu", "ticket", "fog", 17019)]
    [InlineData("net!fog!17010", "rcpu", "fog", 17010)]
    public void AddressGivesTheHostAndPort(string address, string service, string host, int port)
        => Assert.Equal((host, port), Rcpu.Address(address, service));

    [Theory]
    [InlineData("il!fog!rcpu", "bad network il")]
    [InlineData("a!b!c!d", "bad address a!b!c!d")]
    [InlineData("fog!nonesuch", "bad service nonesuch")]
    public void AddressRefusesWhatItCannotDial(string address, string error)
        => Assert.Equal(error, Assert.Throws<FormatException>(() => Rcpu.Address(address, "rcpu")).Message);

    // rcpu's options: -u, -h and -c, -c taking the rest; $cpu the host when -h is not given; and none
    // at all, an unknown one, one without its value, a word after them or no host, a usage error.
    [Theory]
    [InlineData("-h tcp!x!1 -u bob -c ls -l", "bob tcp!x!1 ls,-l")]
    [InlineData("-u bob", "bob tcp!fog!17019 ")]
    [InlineData("-c echo", "glenda tcp!fog!17019 echo")]
    [InlineData("", "glenda tcp!fog!17019 ")]
    [InlineData("-h", null)]
    [InlineData("-x y", null)]
    [InlineData("-u bob extra", null)]
    public void ArgumentsAreRcpusOptions(string args, string? given)
    {
        (string User, string Host, string[] Cmd)? parsed = Rcpu.Arguments(args.Length == 0 ? [] : args.Split(' '), Local(_ => "tcp!fog!17019"));
        Assert.Equal(given, parsed is { } p ? $"{p.User} {p.Host} {string.Join(',', p.Cmd)}" : null);
    }

    [Fact]
    public void WithNoHostAndNoCpuThereIsNoCall() => Assert.Null(Rcpu.Arguments(["-u", "bob"], Local(_ => null)));

    // pwrd's quoting: an empty word, or one with a blank, a control character or a character rc
    // gives a meaning, in quotes with each quote doubled; any other as it is, runes past ASCII too.
    [Theory]
    [InlineData("", "''")]
    [InlineData("plain", "plain")]
    [InlineData("two words", "'two words'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("a\tb", "'a\tb'")]
    [InlineData("a=b", "'a=b'")]
    [InlineData("$x", "'$x'")]
    [InlineData("héllo", "héllo")]
    [InlineData("a!b", "a!b")]
    public void WordQuotesAsPwrdDoes(string word, string quoted) => Assert.Equal(quoted, Rcpu.Word(word));

    [Fact]
    public void ScriptSetsDirAndCmdAsPvarDoes()
    {
        Assert.StartsWith("dir=/x\ncmd=()\nfn server {", Rcpu.Script("/x", []), StringComparison.Ordinal);
        Assert.StartsWith("dir='/a b'\ncmd=ls\n", Rcpu.Script("/a b", ["ls"]), StringComparison.Ordinal);
        Assert.StartsWith("dir=/\ncmd=(echo 'two words')\n", Rcpu.Script("/", ["echo", "two words"]), StringComparison.Ordinal);
        Assert.EndsWith("\t}\n}\nserver\n", Rcpu.Script("/", []), StringComparison.Ordinal);
    }

    // The local directory under /mnt/term/root: / as the root itself, and a Windows path with its
    // drive as the first element, its backslashes forward and no slash after it.
    [Theory]
    [InlineData("/", false, "/mnt/term/root")]
    [InlineData("/usr/glenda", false, "/mnt/term/root/usr/glenda")]
    [InlineData("C:\\work", true, "/mnt/term/root/C:/work")]
    [InlineData("C:\\", true, "/mnt/term/root/C:")]
    public void DirectoryIsUnderMntTermRoot(string directory, bool windows, string expected)
    {
        Local local = Local(_ => null, directory, windows ? new Dictionary<string, string> { ["C:"] = "C:\\" } : new Dictionary<string, string>());
        Assert.Equal(expected, Rcpu.Directory(local));
    }

    private static Local Local(Func<string, string?> environment, string directory = "/", Dictionary<string, string>? drives = null) => new()
    {
        Input = Stream.Null,
        Output = Stream.Null,
        Error = Stream.Null,
        Directory = directory,
        Environment = environment,
        LoginName = "glenda",
        ReadPassword = (_, _) => ValueTask.FromResult<string?>(null),
        ReadConsole = _ => ValueTask.FromResult<string?>(null),
        WriteConsole = (_, _) => ValueTask.CompletedTask,
        Drives = drives ?? new Dictionary<string, string>(),
    };
}
