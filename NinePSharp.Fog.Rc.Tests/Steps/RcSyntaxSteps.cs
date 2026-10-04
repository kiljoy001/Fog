using System.Text;
using NinePSharp.Fog.Rc.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests.Steps;

[Binding]
[Scope(Feature = "rc reads commands as 9front rc does")]
public sealed class RcSyntaxSteps
{
    private const string File = "/tmp/s";
    private string script = string.Empty;

    // A 9front command that makes the same script, where typing it is not possible.
    private string? setup;
    private RcReader? reader;

    [When("rc reads the script")]
    public async Task WhenRead(string text)
    {
        script = text;
        await ReadAsync(Encoding.UTF8.GetBytes(text + "\n"));
    }

    [When(@"^rc reads a script of ""echo "" and a word of (\d+) x's$")]
    public async Task WhenReadLongWord(int length)
    {
        setup = $"{{echo -n 'echo '; for(i in `{{seq {length / 10}}}) echo -n xxxxxxxxxx; echo}} >{File}";
        await ReadAsync(Encoding.ASCII.GetBytes("echo " + new string('x', length) + "\n"));
    }

    [When(@"^rc reads a script of ""(.*)"" and a word of (\d+) x's, then a line of ""(\w+)""$")]
    public async Task WhenReadLongWordThenLine(string prefix, int length, string line)
    {
        setup = $"{{echo -n '{prefix}'; for(i in `{{seq {length / 10}}}) echo -n xxxxxxxxxx; echo; echo {line}}} >{File}";
        await ReadAsync(Encoding.ASCII.GetBytes(prefix + new string('x', length) + "\n" + line + "\n"));
    }

    [Then(@"^rc reports the first (\d+) x's as a token too long, then as a token out of place on line 2$")]
    public Task ThenTooLongThenOutOfPlace(int length) => ThenReports(
        $"{File}:1: token {new string('x', length)}: token buffer too short\n{File}:2: token {new string('x', length)}: syntax error");

    [Then(@"^rc reports the first (\d+) x's as a token too long$")]
    public Task ThenOnlyTooLong(int length) => ThenReports($"{File}:1: token {new string('x', length)}: token buffer too short");

    [When(@"^rc reads a script of (\d+) opening parentheses$")]
    public async Task WhenReadDeep(int count)
    {
        setup = $"{{for(i in `{{seq {count}}}) echo -n '('; echo}} >{File}";
        await ReadAsync(Encoding.ASCII.GetBytes(new string('(', count) + "\n"));
    }

    [When("rc reads a here document holding a NUL byte")]
    public async Task WhenReadNul()
    {
        setup = $"dd -if /dev/zero -of /tmp/z -bs 1 -count 1 >[2]/dev/null; {{echo 'fn f {{cat <<X'; echo -n a; cat /tmp/z; echo; echo X; echo '}}'; echo 'whatis f'}} >{File}";
        await ReadAsync(Encoding.Latin1.GetBytes("fn f {cat <<X\na\0\nX\n}\nwhatis f\n"));
    }

    [When("rc reads a here document holding a 0xFF byte")]
    public async Task WhenReadFf()
    {
        setup = $"{{echo 'fn f {{cat <<X'; echo -n ab; echo -n ÿ | tcs -t latin1; echo cd; echo X; echo '}}'; echo 'whatis f'}} >{File}";
        await ReadAsync(Encoding.Latin1.GetBytes("fn f {cat <<X\nab\u00ffcd\nX\n}\nwhatis f\n"));
    }

    [Then(@"^whatis (\w+) prints$")]
    public async Task ThenWhatis(string name, string expected)
    {
        Assert.Equal(expected, "fn " + name + " " + Text(reader!.Function(name)!));
        await AgreeWithNineFront(setup is null ? script + "\nwhatis " + name : null, expected);
    }

    [Then("rc reports")]
    public async Task ThenReports(string expected)
    {
        Assert.Equal(expected + "\n", reader!.Output);
        await AgreeWithNineFront(setup is null ? script : null, expected);
    }

    [Then(@"^rc reports the first (\d+) x's as a token too long, then a syntax error at end of file$")]
    public Task ThenTooLong(int length) =>
        ThenReports($"{File}:1: token {new string('x', length)}: token buffer too short\n{File}:2: token EOF: syntax error");

    // rc's strings hold bytes; these hold them one per char.
    private static string Text(string bytes) => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(bytes));

    private async Task ReadAsync(byte[] bytes) => reader = await RcReader.ReadAsync(bytes, File, interactive: false);

    // 9front's rc, given the same script, prints the same text. A null text is made by setup.
    private async Task AgreeWithNineFront(string? text, string expected)
    {
        if (await NineFrontOracle.TerminalAsync() is not { } nineFront)
        {
            return;
        }

        await nineFront.RunAsync(text is null ? setup! : "cat >" + File + " <<'EOF9'\n" + text + "\nEOF9");
        Assert.Equal(expected + "\n", await nineFront.RunAsync("@{rfork e; rc " + File + " >[2=1] </dev/null}"));
    }
}
