using System.Text.RegularExpressions;
using NinePSharp.Fog.Gefs.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class SyncQueueSteps(StoreContext context)
{
    private readonly HashSet<long> queued = [];
    private SyncQueue? queue;
    private Task? fence;
    private Task? waiting;

    [Given(@"^a write queue with room for (\d+) blocks, on a device of (\d+) blocks$")]
    public void GivenQueue(int room, long blocks)
    {
        context.Device = new MemoryDevice(blocks);
        queue = new SyncQueue(context.Device, room);
    }

    [When(@"^block (\d+) is queued, and the device holds it$")]
    public void WhenQueuedAndHeld(long block)
    {
        Queue(block);
        context.Device!.WaitHeld("write");
    }

    [When(@"^blocks? (.*) (?:is|are) queued$")]
    public void WhenQueued(string blocks)
    {
        foreach (long block in Numbers(blocks))
        {
            Queue(block);
        }
    }

    [When("a fence is queued")]
    public void WhenFence() => fence = queue!.Fence();

    [Then("the fence is still waiting")]
    public void ThenFenceWaits() => Assert.False(fence!.Wait(TimeSpan.FromMilliseconds(200)));

    [Then("the fence comes out")]
    public void ThenFenceOut() => Finish(fence!);

    [When(@"^block (\d+) starts to be queued$")]
    public void WhenStarts(long block) => waiting = Task.Run(() => Queue(block));

    [Then(@"^block (\d+) is still waiting to be queued$")]
    public void ThenStillWaiting(long block) => Assert.False(waiting!.Wait(TimeSpan.FromMilliseconds(200)), $"block {block} was queued");

    [Then(@"^block (\d+) is queued$")]
    public void ThenQueued(long block) => Finish(waiting!);

    [Then(@"^the device was written at blocks (.*), in that order$")]
    public void ThenWrittenInOrder(string blocks) => Assert.Equal(Numbers(blocks), context.Device!.Written);

    [Then(@"^the device was written at blocks (.*), in some order$")]
    public void ThenWritten(string blocks) => Assert.Equal(Numbers(blocks), context.Device!.Written.Order());

    [Then(@"^blocks (.*) read back from the queue$")]
    public void ThenReadBack(string blocks)
    {
        foreach (long block in Numbers(blocks))
        {
            Assert.Equal(Block(block), queue!.Find(block * Format.BlockSize));
        }
    }

    [Then("no block reads back from the queue")]
    public void ThenNoneReadBack() => Assert.All(queued, block => Assert.Null(queue!.Find(block * Format.BlockSize)));

    private static void Finish(Task task) => task.WaitAsync(Bounded.Limit).GetAwaiter().GetResult();

    private static List<long> Numbers(string list) => [.. Regex.Matches(list, @"\d+").Select(m => long.Parse(m.Value))];

    // A block filled with its own number, so each reads back as itself.
    private static byte[] Block(long block)
    {
        var bytes = new byte[Format.BlockSize];
        Array.Fill(bytes, (byte)block);
        return bytes;
    }

    private void Queue(long block)
    {
        lock (queued)
        {
            queued.Add(block);
        }

        queue!.Write(block * Format.BlockSize, Block(block));
    }
}
