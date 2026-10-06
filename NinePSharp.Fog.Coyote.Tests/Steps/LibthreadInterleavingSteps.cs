using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Specifications;
using NinePSharp.Fog.Coyote.Tests.Support;
using NinePSharp.Fog.Thread;
using Reqnroll;
using Reqnroll.UnitTestProvider;

namespace NinePSharp.Fog.Coyote.Tests.Steps;

[Binding]
[Scope(Feature = "libthread's channels keep their promises in every interleaving Coyote explores")]
public sealed class LibthreadInterleavingSteps(IUnitTestRuntimeProvider runtime, IReqnrollOutputHelper output)
{
    private int size;
    private Func<Task>? race;

    [Given(@"^a channel buffering (\d+) messages$")]
    public void GivenBuffered(int size)
    {
        Require();
        this.size = size;
    }

    [Given("an unbuffered channel")]
    [Given("2 unbuffered channels")]
    public void GivenUnbuffered() => Require();

    [When("2 threads each send 2 messages and 2 threads receive until the channel is closed and empty, while another thread closes it")]
    public void WhenSendersReceiversAndClose() => race = async () =>
    {
        var channel = new Channel<int>(new RendezvousGroup(), size);
        async Task<int[]> SendAsync(int first) => [await channel.SendAsync(first), await channel.SendAsync(first + 1)];
        async Task<List<int>> ReceiveAsync()
        {
            var received = new List<int>();
            for (var (result, value) = await channel.RecvAsync(); result == 1; (result, value) = await channel.RecvAsync())
            {
                received.Add(value);
            }

            return received;
        }

        Task<int[]>[] senders = [Task.Run(() => SendAsync(1)), Task.Run(() => SendAsync(3))];
        Task<List<int>>[] receivers = [Task.Run(ReceiveAsync), Task.Run(ReceiveAsync)];
        await Task.Run(channel.CloseAsync);
        int[] results = (await Task.WhenAll(senders)).SelectMany(each => each).ToArray();
        int[] received = (await Task.WhenAll(receivers)).SelectMany(each => each).Order().ToArray();
        int[] delivered = Enumerable.Range(1, 4).Where(value => results[value - 1] == 1).ToArray();
        Specification.Assert(results.All(result => result is 1 or -1), $"a send returned {string.Join(", ", results)}");
        Specification.Assert(received.SequenceEqual(delivered), $"sends delivered {string.Join(", ", delivered)} but {string.Join(", ", received)} were received");
    };

    [When("a thread alts twice on receiving from either while another thread sends on each")]
    public void WhenAltTwice() => race = async () =>
    {
        var group = new RendezvousGroup();
        Channel<int>[] channels = [new(group), new(group)];
        Task<int>[] sends = [Task.Run(() => channels[0].SendAsync(10)), Task.Run(() => channels[1].SendAsync(11))];
        var received = new List<int>();
        for (int i = 0; i < 2; i++)
        {
            Alt<int>[] entries = [Alt<int>.Recv(channels[0]), Alt<int>.Recv(channels[1])];
            received.Add(entries[await Alt.AltAsync(entries)].Value);
        }

        Specification.Assert((await Task.WhenAll(sends)).All(result => result == 1), "a send did not return 1");
        Specification.Assert(received.Order().SequenceEqual([10, 11]), $"the alts received {string.Join(", ", received)}");
    };

    [When("a thread receives with an interrupt while another sends a message and a third interrupts the receive")]
    public void WhenInterruptedReceive() => race = async () =>
    {
        var channel = new Channel<int>(new RendezvousGroup());
        using var interrupt = new CancellationTokenSource();
        Task<(int Result, int Value)> first = Task.Run(() => channel.RecvAsync(interrupt.Token));
        Task<int> send = Task.Run(() => channel.SendAsync(7));
        await Task.Run(interrupt.Cancel);
        var (result, value) = await first;
        Specification.Assert(result is 1 or -1, $"the interrupted receive returned {result}");
        if (result == -1)
        {
            (result, value) = await channel.RecvAsync();
        }

        Specification.Assert(result == 1 && value == 7, $"the message was received as {result}, {value}");
        Specification.Assert(await send == 1, "the send did not return 1");
    };

    [When("a thread sends a message with an interrupt while another receives and a third interrupts the send")]
    public void WhenInterruptedSend() => race = async () =>
    {
        var channel = new Channel<int>(new RendezvousGroup());
        using var interrupt = new CancellationTokenSource();
        Task<int> send = Task.Run(() => channel.SendAsync(7, interrupt.Token));
        Task<(int Result, int Value)> receive = Task.Run(() => channel.RecvAsync());
        await Task.Run(interrupt.Cancel);
        int sent = await send;
        await channel.CloseAsync();
        var (result, value) = await receive;
        Specification.Assert((sent == 1) == (result == 1), $"the send returned {sent} but the receive {result}");
        Specification.Assert(result != 1 || value == 7, $"the receive got {value}");
    };

    [When("a thread alts on receiving from either until it receives, while another thread closes the first and a third sends on the second")]
    public void WhenAltRacesClose() => race = async () =>
    {
        var group = new RendezvousGroup();
        Channel<int>[] channels = [new(group), new(group)];
        Task closed = Task.Run(channels[0].CloseAsync);
        Task<int> send = Task.Run(() => channels[1].SendAsync(5));
        Alt<int>[] entries = [Alt<int>.Recv(channels[0]), Alt<int>.Recv(channels[1])];
        int chosen = await Alt.AltAsync(entries);
        if (chosen == 0)
        {
            Specification.Assert(entries[0].Error is not null, "the closed channel's entry was chosen without an error");
            chosen = await Alt.AltAsync(entries);
        }

        await closed;
        Specification.Assert(chosen == 1 && entries[1].Value == 5, $"the alt chose {chosen} with {entries[1].Value}");
        Specification.Assert(await send == 1, "the send did not return 1");
    };

    [Then("in every explored schedule each message whose send returned 1 is received exactly once, and no other is received")]
    [Then("in every explored schedule the alts receive one message from each channel")]
    [Then("in every explored schedule the message is received exactly once and its send returns 1")]
    [Then("in every explored schedule the message is received if and only if its send returned 1")]
    [Then("in every explored schedule the message on the second channel is received exactly once and its send returns 1")]
    public void ThenExplored() => Explorer.Explore(race!, output);

    private void Require()
    {
        if (!RewritingEngine.IsAssemblyRewritten(typeof(Channel).Assembly))
        {
            runtime.TestIgnore("the Fog assemblies were not rewritten with coyote rewrite");
        }
    }
}
