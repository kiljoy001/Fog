using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Thread.Tests.Steps;

[Binding]
public sealed class RendezvousSteps : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(50);
    private readonly List<Task<object?>> meetings = new();
    private readonly CancellationTokenSource interrupt = new();
    private object? tag;
    private object? other;

    public void Dispose() => interrupt.Dispose();

    [Given("a tag")]
    public void GivenTag() => tag = new string('t', 3);

    [Given("another tag equal to it but a different object")]
    public void GivenEqualTag()
    {
        other = new string('t', 3);
        Assert.Equal(tag, other);
    }

    [When(@"^(?:a|another) thread rendezvouses on the tag with ""(.*)""$")]
    public void WhenMeet(string value) => meetings.Add(Rendezvous.MeetAsync(tag!, value));

    [When(@"^another thread rendezvouses on the other tag with ""(.*)""$")]
    public void WhenMeetOther(string value) => meetings.Add(Rendezvous.MeetAsync(other!, value));

    [When(@"^a thread rendezvouses on the tag with ""(.*)"" and is interrupted$")]
    public async Task WhenInterrupted(string value)
    {
        meetings.Add(Rendezvous.MeetAsync(tag!, value, interrupt.Token));
        await Task.Delay(Settle);
        await interrupt.CancelAsync();
    }

    [Then("that rendezvous has not finished")]
    public async Task ThenWaits()
    {
        await Task.Delay(Settle);
        Assert.False(meetings[^1].IsCompleted);
    }

    [Then("neither rendezvous has finished")]
    public async Task ThenNeither()
    {
        await Task.Delay(Settle);
        Assert.All(meetings, meeting => Assert.False(meeting.IsCompleted));
    }

    [Then(@"^the second rendezvous returns ""(.*)"" at once$")]
    public void ThenSecondReturns(string value)
    {
        Assert.True(meetings[1].IsCompleted);
        Assert.Equal(value, meetings[1].Result);
    }

    [Then(@"^the first rendezvous returns ""(.*)""$")]
    public async Task ThenFirstReturns(string value) => Assert.Equal(value, await meetings[0].WaitAsync(Wait));

    [Then("that rendezvous returns ~0")]
    public async Task ThenInterrupted() => Assert.Same(Rendezvous.Interrupted, await meetings[^1].WaitAsync(Wait));
}
