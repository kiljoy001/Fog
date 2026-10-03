using System.Text;
using NinePSharp.Fog.Rc.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests.Steps;

[Binding]
[Scope(Feature = "rc reads interactively as 9front rc -i does")]
public sealed class RcInteractiveSteps
{
    private string script = string.Empty;
    private RcReader? reader;

    [When("rc -i reads the script")]
    public void WhenRead(string text)
    {
        script = text;
        reader = RcReader.Read(Encoding.UTF8.GetBytes(text + "\n"), "/fd/0", interactive: true);
    }

    [Then("it prints")]
    public async Task ThenPrints(string expected)
    {
        Assert.Equal(expected, reader!.Output);
        if (await NineFrontOracle.TerminalAsync() is { } nineFront)
        {
            await nineFront.RunAsync("cat >/tmp/s <<'EOF9'\n" + script + "\nEOF9");
            Assert.Equal(expected, await nineFront.RunAsync("@{rfork e; prompt=('%' '+') rc -i </tmp/s >[2=1]}"));
        }
    }
}
