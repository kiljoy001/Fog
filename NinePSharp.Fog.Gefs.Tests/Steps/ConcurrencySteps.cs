using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Gefs.Tests.Support;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class ConcurrencySteps(StoreContext context)
{
    private Task? change;
    private Task? commit;
    private Task? second;
    private int epoch = -1;
    private Tree? taken;
    private HashSet<long> freed = [];
    private ulong sequence;

    [Given("the server commits")]
    [When("the server commits")]
    public void WhenCommits() => Finish(Task.Run(context.Server!.Sync));

    [When(@"^adm starts writing ""(.*)"" to a new file (\S+), and the change stops at its first device write$")]
    public void WhenChangeHeld(string text, string name)
    {
        GefsFs fs = context.Files!;
        context.Device!.HoldNext("write");
        change = Task.Run(async () =>
        {
            ResourceOpenHandle open = await fs.CreateAndOpenAsync(fs.Root, name, 0b110_110_110, NinePConstants.ORDWR, Context(), default);
            await fs.WriteAsync(open, 0, Encoding.ASCII.GetBytes(text), Context(), default);
            await fs.ClunkAsync(open, Context(), default);
        });
        context.Device.WaitHeld();
    }

    [When(@"^the device lets the (?:write|flush) go$")]
    public void WhenLet() => context.Device!.Let();

    [Then("the change finishes")]
    public void ThenChangeFinishes() => Finish(change!);

    [When("a commit starts, and stops at its first device flush")]
    public void WhenCommitHeld()
    {
        context.Device!.HoldNext("flush");
        commit = Task.Run(context.Server!.Sync);
        context.Device.WaitHeld();
    }

    [When("a second commit starts")]
    public void WhenSecondCommit() => second = Task.Run(context.Server!.Sync);

    [Then("the second commit is still waiting")]
    public void ThenSecondWaits() => Assert.False(second!.Wait(TimeSpan.FromMilliseconds(200)));

    [Then("the commit finishes")]
    public void ThenCommitFinishes() => Finish(commit!);

    [Then("the second commit finishes")]
    public void ThenSecondFinishes() => Finish(second!);

    [When("a read takes main's root")]
    public void WhenReadTakesRoot()
    {
        epoch = context.Store!.Allocator.Epochs.Start();
        taken = context.Store.Mount("main").Pinned();
    }

    [When("the read finishes")]
    public void WhenReadFinishes() => context.Store!.Allocator.Epochs.End(epoch);

    [Then(@"^the read that took main's root reads notes as ""(.*)""$")]
    public void ThenTakenReads(string text)
    {
        var entry = Dir.Read(Keys.Entry(0, "notes"), taken!.Lookup(Keys.Entry(0, "notes"))!);
        byte[] block = context.Store!.Allocator.ReadData(Bptr.Read(taken.Lookup(Keys.Data(entry.Qid.Path, 0))!));
        Assert.Equal(text, Encoding.ASCII.GetString(block, 0, (int)entry.Length));
    }

    [Then(@"^the blocks main's root used, and the change freed, are (not free|free)$")]
    public void ThenFreed(string state)
    {
        if (freed.Count == 0)
        {
            freed = Accounting.Used(taken!);
            freed.ExceptWith(Accounting.Held(context.Store!));
            Assert.NotEmpty(freed);
        }

        var free = context.Store!.Allocator.Arenas.SelectMany(a => a.Free.SelectMany(r => Enumerable.Range(0, (int)(r.Length / Format.BlockSize)).Select(k => r.Offset + (k * Format.BlockSize)))).ToHashSet();
        Assert.Equal(state == "free", freed.IsSubsetOf(free));
        Assert.Equal(state == "free", !freed.Overlaps(context.Store.Allocator.Epochs.Waiting));
    }

    [Then("no block is waiting for reads")]
    public void ThenNoneWaiting() => Assert.Empty(context.Store!.Allocator.Epochs.Waiting);

    private static void Finish(Task task) => task.WaitAsync(Bounded.Limit).GetAwaiter().GetResult();

    private ResourceOperationContext Context() => new(new ResourceOperationId("concurrency", Interlocked.Increment(ref sequence)), 1, "adm");
}
