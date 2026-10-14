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
    private readonly Dictionary<int, int> reads = [];
    private readonly Dictionary<int, HashSet<long>> changes = [];
    private Task? change;
    private Task? commit;
    private Task? second;
    private Task? open;
    private Task<(Task<uint> First, Task<uint> Second)>? asks;
    private int epoch = -1;
    private Tree? taken;
    private HashSet<long> freed = [];
    private ulong sequence;

    [Given("the server commits")]
    [When("the server commits")]
    public void WhenCommits() => Finish(Task.Run(context.Server!.Sync));

    [When(@"^the device holds its next (write|flush)$")]
    public void WhenHolds(string operation) => context.Device!.HoldNext(operation);

    [When(@"^adm starts writing ""(.*)"" to a new file (\S+), and the change waits for room in the write queue$")]
    public void WhenChangeHeld(string text, string name)
    {
        GefsFs fs = context.Files!;
        change = Task.Run(async () =>
        {
            ResourceOpenHandle open = await fs.CreateAndOpenAsync(fs.Root, name, 0b110_110_110, NinePConstants.ORDWR, Context(), default);
            await fs.WriteAsync(open, 0, Encoding.ASCII.GetBytes(text), Context(), default);
            await fs.ClunkAsync(open, Context(), default);
        });
        context.Device!.WaitHeld("write");
        Assert.False(change.Wait(TimeSpan.FromMilliseconds(200)));
    }

    [When(@"^the device lets the (write|flush) go$")]
    public void WhenLet(string operation) => context.Device!.Let(operation);

    [When(@"^adm asks to write ""(.*)"" to notes at offset 0, and then ""(.*)""$")]
    public void WhenAsks(string first, string second)
    {
        ResourceOpenHandle opened = Opened(NinePConstants.OWRITE);
        Ask(opened, first, opened, second);
    }

    [When(@"^adm asks to write ""(.*)"" to a file opened only to read, and then ""(.*)"" to notes at offset 0$")]
    public void WhenAsksRefused(string first, string second)
        => Ask(Opened(NinePConstants.OREAD), first, Opened(NinePConstants.OWRITE), second);

    [Then("the asks have returned, and both writes are still waiting")]
    public void ThenAsksWaiting()
    {
        var (first, second) = Asked();
        Assert.False(Task.WhenAll(first, second).Wait(TimeSpan.FromMilliseconds(200)));
    }

    [Then("both writes finish")]
    public void ThenWritesFinish()
    {
        var (first, second) = Asked();
        Finish(Task.WhenAll(first, second));
    }

    [Then(@"^the first write fails with ""(.*)""$")]
    public void ThenFirstFails(string error) => Assert.Equal(error, Assert.Throws<IOException>(() => Finish(Asked().First)).Message);

    [Then("the second write finishes")]
    public void ThenSecondWriteFinishes() => Finish(Asked().Second);

    [Then(@"^the change fails with ""(.*)""$")]
    public void ThenChangeFails(string error) => Assert.Equal(error, Assert.Throws<GefsException>(() => Finish(change!)).Message);

    [Then("the change finishes")]
    public void ThenChangeFinishes() => Finish(change!);

    [When(@"^adm starts opening (\S+) with truncation$")]
    public void WhenTruncating(string name)
    {
        GefsFs fs = context.Files!;
        open = Task.Run(async () =>
        {
            ResourceHandle file = (await fs.WalkAsync(fs.Root, name, default))!;
            await fs.OpenAsync(file, NinePConstants.OWRITE | NinePConstants.OTRUNC, Context(), default);
        });
    }

    [Then("the open is still waiting")]
    public void ThenOpenWaits() => Assert.False(open!.Wait(TimeSpan.FromMilliseconds(200)));

    [Then("the open finishes")]
    public void ThenOpenFinishes() => Finish(open!);

    [When(@"^read (\d+) takes main's root$")]
    public void WhenNumberedRead(int read) => reads[read] = context.Store!.Allocator.Epochs.Start();

    [When(@"^read (\d+) finishes$")]
    public void WhenNumberedReadFinishes(int read) => context.Store!.Allocator.Epochs.End(reads[read]);

    [When(@"^change (\d+) writes ""(.*)"" to notes, and the server commits$")]
    public void WhenNumberedChange(int number, string text)
    {
        GefsFs fs = context.Files!;
        HashSet<long> used = Accounting.Used(context.Store!.Mount("main").Pinned());
        Finish(Task.Run(async () =>
        {
            ResourceHandle file = (await fs.WalkAsync(fs.Root, "notes", default))!;
            ResourceOpenHandle opened = await fs.OpenAsync(file, NinePConstants.OWRITE, Context(), default);
            await fs.WriteAsync(opened, 0, Encoding.ASCII.GetBytes(text), Context(), default);
            await fs.ClunkAsync(opened, Context(), default);
        }));
        WhenCommits();
        used.ExceptWith(Accounting.Held(context.Store));
        Assert.NotEmpty(used);
        changes[number] = used;
    }

    [Then(@"^the blocks change (\d+) freed are (free|waiting for reads)$")]
    public void ThenChangeFreed(int number, string state)
    {
        var free = context.Store!.Allocator.Arenas.SelectMany(a => a.Free.SelectMany(r => Enumerable.Range(0, (int)(r.Length / Format.BlockSize)).Select(k => r.Offset + (k * Format.BlockSize)))).ToHashSet();
        Assert.True((state == "free" ? free : context.Store.Allocator.Epochs.Waiting.ToHashSet()).IsSupersetOf(changes[number]));
    }

    [When("a commit starts, and stops at its first device flush")]
    public void WhenCommitHeld()
    {
        context.Device!.HoldNext("flush");
        commit = Task.Run(context.Server!.Sync);
        context.Device.WaitHeld("flush");
    }

    [When("a commit starts")]
    public void WhenCommitStarts() => commit = Task.Run(context.Server!.Sync);

    [Then("the commit is still waiting")]
    public void ThenCommitWaits() => Assert.False(commit!.Wait(TimeSpan.FromMilliseconds(200)));

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

    private ResourceOpenHandle Opened(byte mode)
    {
        GefsFs fs = context.Files!;
        return Bounded.Run(async () => await fs.OpenAsync((await fs.WalkAsync(fs.Root, "notes", default))!, mode, Context(), default));
    }

    // Both writes asked for from one task, one after the other; asking returns before they run.
    private void Ask(ResourceOpenHandle first, string firstText, ResourceOpenHandle second, string secondText)
    {
        GefsFs fs = context.Files!;
        asks = Task.Run(() => (
            fs.WriteAsync(first, 0, Encoding.ASCII.GetBytes(firstText), Context(), default).AsTask(),
            fs.WriteAsync(second, 0, Encoding.ASCII.GetBytes(secondText), Context(), default).AsTask()));
    }

    private (Task<uint> First, Task<uint> Second) Asked()
    {
        Assert.True(asks!.Wait(Bounded.Limit), "asking for the writes did not return");
        return asks.Result;
    }

    private ResourceOperationContext Context() => new(new ResourceOperationId("concurrency", Interlocked.Increment(ref sequence)), 1, "adm");
}
