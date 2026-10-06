using NinePSharp.Fog.Thread;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Thread.Tests.Steps;

[Binding]
public sealed class ChannelSteps : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(50);
    private readonly List<Channel<int>> channels = new();
    private readonly List<Task<int>> sends = new();
    private readonly List<Task<(int Result, int Value)>> receives = new();
    private readonly List<int> received = new();
    private readonly CancellationTokenSource interrupt = new();
    private readonly RendezvousGroup group = new();
    private readonly int[] chosen = new int[2];
    private IReadOnlyList<Alt> entries = [];
    private Task<int>? alt;
    private Task? sequence;
    private int immediate;
    private int immediateValue;

    public void Dispose()
    {
        foreach (var channel in channels)
        {
            _ = channel.CloseAsync();
        }

        interrupt.Dispose();
    }

    [Given("an unbuffered channel")]
    public void GivenUnbuffered() => channels.Add(new Channel<int>(group));

    [Given(@"^(\d+) unbuffered channels$")]
    public void GivenUnbufferedChannels(int count)
    {
        for (int i = 0; i < count; i++)
        {
            channels.Add(new Channel<int>(group));
        }
    }

    [Given("2 unbuffered channels in different rendezvous groups")]
    public void GivenSeparateGroups()
    {
        channels.Add(new Channel<int>(group));
        channels.Add(new Channel<int>(new RendezvousGroup()));
    }

    [Given(@"^a channel buffering (\d+) messages?$")]
    public void GivenBuffered(int size) => channels.Add(new Channel<int>(group, size));

    [Given("a full channel buffering 1 message")]
    public async Task GivenFull()
    {
        channels.Add(new Channel<int>(group, 1));
        Assert.Equal(1, await channels[0].NbSendAsync(0));
    }

    [Given("2 channels each buffering 1 message, both holding a message")]
    public async Task GivenTwoHolding()
    {
        for (int i = 0; i < 2; i++)
        {
            channels.Add(new Channel<int>(group, 1));
            Assert.Equal(1, await channels[i].NbSendAsync(i));
        }
    }

    [When(@"^a thread sends (\d+)$")]
    [When(@"^another thread sends (\d+)$")]
    public void WhenSend(int value) => sends.Add(channels[0].SendAsync(value));

    [When(@"^another thread sends (\d+) on the second channel$")]
    public void WhenSendSecond(int value) => sends.Add(channels[1].SendAsync(value));

    [When("a thread sends 1, 2 and 3 in turn")]
    public void WhenSendThree() => sequence = SendInTurnAsync(1, 2, 3);

    [When("a thread sends 1 and 2 in turn")]
    public Task WhenSendTwo() => SendInTurnAsync(1, 2).WaitAsync(Wait);

    [When("a thread receives")]
    [When("another thread receives")]
    public void WhenReceive() => receives.Add(channels[0].RecvAsync());

    [When(@"^another thread receives (\d+) times$")]
    public async Task WhenReceiveTimes(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var (result, value) = await channels[0].RecvAsync().WaitAsync(Wait);
            Assert.Equal(1, result);
            received.Add(value);
        }
    }

    [When(@"^(?:a|another) thread tries a non-blocking (send|recv)$")]
    public async Task WhenNonBlocking(string operation)
    {
        if (operation == "send")
        {
            immediate = await channels[0].NbSendAsync(6);
            return;
        }

        (immediate, immediateValue) = await channels[0].NbRecvAsync();
    }

    [When(@"^a thread alts on receiving from both, (\d+) times, refilling the chosen channel each time$")]
    public async Task WhenAltMany(int times)
    {
        for (int i = 0; i < times; i++)
        {
            int index = await Alt.AltAsync([Alt<int>.Recv(channels[0]), Alt<int>.Recv(channels[1])]).WaitAsync(Wait);
            chosen[index]++;
            Assert.Equal(1, await channels[index].NbSendAsync(index));
        }
    }

    [When("a thread alts on receiving from it, terminated by CHANNOBLK")]
    public async Task WhenNbAlt() => immediate = await Alt.NbAltAsync([Alt<int>.Recv(channels[0])]);

    [When("a thread alts on a CHANNOP entry and on receiving from each channel")]
    public void WhenAltNop()
    {
        entries = [Alt.Nop, Alt<int>.Recv(channels[0]), Alt<int>.Recv(channels[1])];
        alt = Alt.AltAsync(entries);
    }

    [When(@"^a thread alts on sending (\d+) on it$")]
    public void WhenAltSend(int value)
    {
        entries = [Alt<int>.Send(channels[0], value)];
        alt = Alt.AltAsync(entries);
    }

    [When("a thread alts on receiving from each channel")]
    public void WhenAltEach()
    {
        entries = [Alt<int>.Recv(channels[0]), Alt<int>.Recv(channels[1])];
        alt = Alt.AltAsync(entries);
    }

    [Then(@"^the alt is refused because ""(.*)""$")]
    public async Task ThenRefused(string reason) => Assert.StartsWith(reason, (await Assert.ThrowsAsync<ArgumentException>(() => alt!)).Message, StringComparison.Ordinal);

    [When("the thread alts with the same entries again")]
    public void WhenAltAgain() => alt = Alt.AltAsync(entries);

    [When("the channel is closed")]
    [When("the first channel is closed")]
    public async Task WhenClose() => Assert.Equal(0, await channels[0].CloseAsync().WaitAsync(Wait));

    [When("the second channel is closed")]
    public async Task WhenCloseSecond() => Assert.Equal(0, await channels[1].CloseAsync().WaitAsync(Wait));

    [When(@"^a thread sends (\d+) and is interrupted before anyone receives$")]
    public async Task WhenInterrupted(int value)
    {
        sends.Add(channels[0].SendAsync(value, interrupt.Token));
        await Task.Delay(Settle);
        await interrupt.CancelAsync();
    }

    [Then("the send has not finished")]
    public async Task ThenSendWaits()
    {
        await Task.Delay(Settle);
        Assert.False(sends[^1].IsCompleted);
    }

    [Then("the receive has not finished")]
    public async Task ThenReceiveWaits()
    {
        await Task.Delay(Settle);
        Assert.False(receives[^1].IsCompleted);
    }

    [Then("the alt has not finished")]
    public async Task ThenAltWaits()
    {
        await Task.Delay(Settle);
        Assert.False(alt!.IsCompleted);
    }

    [Then(@"^the receive gets (\d+) and returns 1$")]
    public async Task ThenReceived(int value) => Assert.Equal((1, value), await receives[^1].WaitAsync(Wait));

    [Then(@"^the receive returns (-?\d+)$")]
    public async Task ThenReceiveReturns(int result) => Assert.Equal(result, (await receives[^1].WaitAsync(Wait)).Result);

    [Then(@"^the send returns (-?\d+)$")]
    [Then(@"^the third send returns (-?\d+)$")]
    [Then(@"^the fourth send returns (-?\d+)$")]
    public async Task ThenSendReturns(int result)
    {
        if (sequence is not null)
        {
            await sequence.WaitAsync(Wait);
        }

        Assert.Equal(result, await sends[^1].WaitAsync(Wait));
    }

    [Then("the first 2 sends have returned 1 and the third has not finished")]
    public async Task ThenTwoOfThree()
    {
        await Task.Delay(Settle);
        Task<int>[] started;
        lock (sends)
        {
            started = sends.ToArray();
        }

        Assert.Equal(3, started.Length);
        Assert.Equal(1, await started[0].WaitAsync(Wait));
        Assert.Equal(1, await started[1].WaitAsync(Wait));
        Assert.False(started[2].IsCompleted);
    }

    [Then(@"^it gets (\d+), (\d+) and (\d+) in order$")]
    public void ThenInOrder(int first, int second, int third) => Assert.Equal([first, second, third], received);

    [Then(@"^it returns (\d+) at once$")]
    public void ThenImmediate(int result) => Assert.Equal(result, immediate);

    [Then(@"^the waiting thread's receive returns 1 with (\d+)$")]
    public async Task ThenWaitingReceive(int value) => Assert.Equal((1, value), await receives[^1].WaitAsync(Wait));

    [Then(@"^the waiting thread's send returns 1 with (\d+)$")]
    public async Task ThenWaitingSend(int value)
    {
        Assert.Equal(1, await sends[^1].WaitAsync(Wait));
        Assert.Equal(value, immediateValue);
    }

    [When(@"^a thread alts on (\d+) CHANNOP entries$")]
    public void WhenAltNops(int count)
    {
        entries = Enumerable.Repeat(Alt.Nop, count).ToArray();
        alt = Alt.AltAsync(entries, interrupt.Token);
    }

    [When("the alt is interrupted")]
    public Task WhenAltInterrupted() => interrupt.CancelAsync();

    [Then("neither send has finished")]
    public async Task ThenNeitherSend()
    {
        await Task.Delay(Settle);
        Assert.All(sends, send => Assert.False(send.IsCompleted));
    }

    [Then(@"^it gets (\d+) and (\d+) in some order$")]
    public void ThenAnyOrder(int first, int second) => Assert.Equal([first, second], received.Order());

    [Then("both sends return 1")]
    public async Task ThenBothSent()
    {
        foreach (var send in sends)
        {
            Assert.Equal(1, await send.WaitAsync(Wait));
        }
    }

    [Then("chanclosing still reports the channel open")]
    public void ThenStillOpen() => Assert.Equal(-1, channels[0].Closing());

    [When("a thread sends 1, 2, 3 and 4, receiving one message after the second")]
    public void WhenSendWrapping() => sequence = SendWrappingAsync();

    [Then("the sends of 1, 2 and 3 have returned 1 and the send of 4 has not finished")]
    public async Task ThenThreeOfFour()
    {
        await Task.Delay(Settle);
        Task<int>[] started;
        lock (sends)
        {
            started = sends.ToArray();
        }

        Assert.Equal(4, started.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(1, await started[i].WaitAsync(Wait));
        }

        Assert.False(started[3].IsCompleted);
    }

    [Then(@"^a receive gets (\d+) and returns 1$")]
    public async Task ThenReceiveGets(int value) => Assert.Equal((1, value), await channels[0].RecvAsync().WaitAsync(Wait));

    [Then("each channel is chosen at least once")]
    public void ThenFair() => Assert.All(chosen, count => Assert.True(count > 0, $"chosen {string.Join(" and ", chosen)} times"));

    [Then(@"^alt returns (\d+), the index of the terminator, at once$")]
    public void ThenTerminator(int index) => Assert.Equal(index, immediate);

    [Then(@"^alt returns (\d+) and its entry received (\d+)$")]
    public async Task ThenAltReceived(int index, int value)
    {
        Assert.Equal(index, await alt!.WaitAsync(Wait));
        Assert.Equal(value, ((Alt<int>)entries[index]).Value);
    }

    [Then(@"^alt returns 0 and the receive gets (\d+)$")]
    public async Task ThenAltSent(int value)
    {
        Assert.Equal(0, await alt!.WaitAsync(Wait));
        Assert.Equal((1, value), await receives[^1].WaitAsync(Wait));
    }

    [Then(@"^alt returns (\d+) and that entry's error says the channel was closed$")]
    public async Task ThenAltClosed(int index)
    {
        Assert.Equal(index, await alt!.WaitAsync(Wait));
        Assert.Equal("channel was closed", entries[index].Error);
    }

    [Then("alt returns -1")]
    public async Task ThenAltFails() => Assert.Equal(-1, await alt!.WaitAsync(Wait));

    [Then("another send returns -1 at once")]
    public void ThenSendFailsAtOnce()
    {
        Task<int> send = channels[0].SendAsync(2);
        Assert.True(send.IsCompleted);
        Assert.Equal(-1, send.Result);
    }

    [Then(@"^(\d+) receives get 1 and 2$")]
    public async Task ThenReceivesGet(int count)
    {
        for (int value = 1; value <= count; value++)
        {
            Assert.Equal((1, value), await channels[0].RecvAsync().WaitAsync(Wait));
        }
    }

    [Then("another receive returns -1 at once")]
    public void ThenReceiveFailsAtOnce()
    {
        var receive = channels[0].RecvAsync();
        Assert.True(receive.IsCompleted);
        Assert.Equal(-1, receive.Result.Result);
    }

    [Then(@"^chanclosing returns (-?\d+)$")]
    public void ThenClosing(int result) => Assert.Equal(result, channels[0].Closing());

    [Then("closing it again returns -1")]
    public async Task ThenCloseAgain() => Assert.Equal(-1, await channels[0].CloseAsync());

    private async Task SendWrappingAsync()
    {
        await SendInTurnAsync(1, 2);
        Assert.Equal((1, 1), await channels[0].RecvAsync());
        await SendInTurnAsync(3, 4);
    }

    private async Task SendInTurnAsync(params int[] values)
    {
        foreach (int value in values)
        {
            Task<int> send = channels[0].SendAsync(value);
            lock (sends)
            {
                sends.Add(send);
            }

            await send;
        }
    }
}
