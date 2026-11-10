using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class UserSteps
{
    private readonly StringWriter console = new();
    private Users? users;
    private string? reamed;
    private string? failure;

    [When(@"^the table is reamed for ""(.*)""$")]
    public void WhenReamed(string user) => reamed = Users.Ream(user);

    [Then(@"^the reamed table is ""(.*)""$")]
    public void ThenReamed(string text) => Assert.Equal(Unescape(text), reamed);

    [Given(@"^the user table ""([^""]*)""$")]
    public void GivenTable(string text) => users = Users.Parse(Unescape(text), console);

    [When(@"^the user table ""([^""]*)"" is parsed$")]
    public void WhenParsed(string text)
    {
        try
        {
            users = Users.Parse(Unescape(text), console);
        }
        catch (GefsException error)
        {
            failure = error.Message;
        }
    }

    [Then(@"^""(.*)"" (is|is not) in the group ""(.*)""$")]
    public void ThenInGroup(string user, string @is, string group) => Assert.Equal(@is == "is", users!.InGroup(user, group));

    [Then(@"^the parse fails with ""(.*)""$")]
    public void ThenFails(string error) => Assert.Equal(error, failure);

    [Then("the parse succeeds")]
    public void ThenSucceeds() => Assert.True(failure is null && users is not null, failure);

    [Then(@"^the console says ""([^""]*)""$")]
    public void ThenConsole(string text) => Assert.Equal(Unescape(text), console.ToString());

    private static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal);
}
