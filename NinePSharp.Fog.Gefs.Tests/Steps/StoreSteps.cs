using System.Text;
using NinePSharp.Fog.Gefs.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class StoreSteps
{
    private const long B = Format.BlockSize;

    private readonly SortedDictionary<int, string> model = [];
    private readonly Dictionary<long, Dictionary<int, string>> committed = [];
    private MemoryDevice? device;
    private Store? store;
    private int version;
    private string? failure;

    [Given(@"^a device of (\d+) blocks reamed with (\d+) arenas$")]
    public void GivenReamed(long blocks, int arenas)
    {
        device = new MemoryDevice(blocks);
        store = Store.Ream(device, arenas);
        committed[store.Generation] = new(model);
    }

    [Then(@"^both superblocks name (\d+) arenas and a root tree (\d+) high, committed at generation (\d+)$")]
    public void ThenSuperblocks(int arenas, int height, long gen)
    {
        Superblock first = Superblock.Read(device!.Block(0));
        Superblock backup = Superblock.Read(device.Block((device.Size / B) - 1));
        Assert.Equal(device.Block(0), device.Block((device.Size / B) - 1));
        Assert.Equal((arenas, height, gen, gen + 1, Format.BlockSize, Format.BufferSpace), (first.Arenas.Length, first.Snap.Height, first.SyncGen, first.NextGen, first.BlockSize, first.BufferSpace));
        Assert.Equal(store!.Allocator.Arenas.Select(a => a.Pointer.Addr), backup.Arenas.Select(a => a.Addr));
    }

    [When("the device is opened")]
    public void WhenOpened()
    {
        failure = null;
        try
        {
            store = Store.Open(device!);
        }
        catch (GefsException error)
        {
            failure = error.Message;
        }
    }

    [Then(@"^the store is at generation (\d+) and its root tree holds (keys .*|nothing)$")]
    public void ThenHolds(long gen, string keys)
    {
        Assert.Null(failure);
        Assert.Equal(gen, store!.Generation);
        var expected = keys == "nothing" ? [] : Numbers(keys.Split(' ', 2)[1]).ToList();
        Assert.Equal(expected, Scan().Select(kv => kv.Key));
        foreach (var (key, value) in Scan())
        {
            Assert.Equal(committed[gen][key], value);
        }
    }

    [Then(@"^opening fails with ""(.*)""$")]
    public void ThenOpeningFails(string message) => Assert.Equal(message, failure);

    [When(@"^keys (.*) are inserted and the store commits$")]
    public void WhenInsertedAndCommitted(string keys)
    {
        WhenInserted(keys);
        Commit();
    }

    [When(@"^keys (.*) are inserted again and the store commits$")]
    public void WhenInsertedAgain(string keys) => WhenInsertedAndCommitted(keys);

    [When(@"^keys (.*) are deleted and the store commits$")]
    public void WhenDeletedAndCommitted(string keys)
    {
        foreach (int n in Numbers(keys))
        {
            store!.Root.Upsert(new Message(MessageOp.Delete, Key(n), []));
            model.Remove(n);
        }

        Commit();
    }

    [When(@"^keys (.*) are inserted$")]
    public void WhenInserted(string keys)
    {
        foreach (int n in Numbers(keys))
        {
            string value = $"{n}:{++version}:" + new string('v', 80);
            store!.Root.Upsert(new Message(MessageOp.Insert, Key(n), Encoding.ASCII.GetBytes(value)));
            model[n] = value;
        }
    }

    [When(@"^keys (.*) are inserted and keys (.*) deleted$")]
    public void WhenInsertedAndDeleted(string inserted, string deleted)
    {
        WhenInserted(inserted);
        foreach (int n in Numbers(deleted))
        {
            store!.Root.Upsert(new Message(MessageOp.Delete, Key(n), []));
            model.Remove(n);
        }
    }

    [When(@"^the store commits, the device crashing after (\d+) of the commit's writes$")]
    public void WhenCrashingCommit(int writes)
    {
        device!.CrashAfter(writes);
        Commit();
        device.Restart();
    }

    [When("the device's record is cleared and the store commits")]
    public void WhenTracedCommit()
    {
        device!.Trace.Clear();
        Commit();
    }

    [Then(@"^the device recorded: (.*)$")]
    public void ThenRecorded(string events) => Assert.Equal(events.Split(", "), device!.Trace);

    [When(@"^(\d+) qids are taken and the store commits$")]
    public void WhenQids(int count)
    {
        for (int i = 0; i < count; i++)
        {
            store!.NewQid();
        }

        Commit();
    }

    [Then(@"^the next qid is (\d+)$")]
    public void ThenNextQid(long qid) => Assert.Equal(qid, store!.NewQid());

    [Then(@"^a block allocated now is born in generation (\d+)$")]
    public void ThenBorn(long gen) => Assert.Equal(gen, store!.Allocator.New(BlockType.Leaf).Gen);

    [When(@"^the (first|backup|both) superblocks? (?:is|are) (damaged|written by another version)$")]
    public void WhenSuperblockChanged(string which, string change)
    {
        long last = (device!.Size / B) - 1;
        foreach (long n in which switch { "first" => [0L], "backup" => [last], _ => new[] { 0L, last } })
        {
            if (change == "damaged")
            {
                device.Damage(n);
            }
            else
            {
                byte[] block = device.Block(n);
                "gefs9.00"u8.CopyTo(block);
                device.Put(n, block);
            }
        }
    }

    [When(@"^both superblocks are rewritten claiming (blocks|buffers) of (\d+) bytes$")]
    public void WhenRewritten(string what, int size)
    {
        long last = (device!.Size / B) - 1;
        Superblock sb = Superblock.Read(device.Block(0));
        sb = what == "blocks" ? sb with { BlockSize = size } : sb with { BufferSpace = size };
        var block = new byte[B];
        sb.Write(block);
        device.Put(0, block);
        device.Put(last, block);
    }

    [Then("every block of the device is in its root tree, in a log or free")]
    public void ThenAccounted()
    {
        var reached = new List<long>();
        Walk(store!.Root.Root, store.Root.Height, reached);
        foreach (Arena a in store.Allocator.Arenas)
        {
            reached.AddRange(a.LogAddresses());
            foreach (var (offset, length) in a.Free)
            {
                for (long at = offset; at < offset + length; at += B)
                {
                    reached.Add(at);
                }
            }
        }

        var data = store.Allocator.Arenas.SelectMany(a => Enumerable.Range(0, (int)(a.Size / B)).Select(k => a.Start + (k * B)));
        Assert.Equal(data.Order(), reached.Order());
    }

    private static byte[] Key(int n) => [0x10, (byte)(n >> 8), (byte)n];

    private static IEnumerable<int> Numbers(string text)
    {
        string[] ends = text.Split(" to ");
        return Enumerable.Range(int.Parse(ends[0]), int.Parse(ends[^1]) - int.Parse(ends[0]) + 1);
    }

    private void Walk(Bptr bp, int height, List<long> reached)
    {
        reached.Add(bp.Addr);
        Blk b = store!.Allocator.Get(bp);
        for (int i = 0; height > 1 && i < b.ValueCount; i++)
        {
            Walk(Blk.GetPointer(b.GetValue(i).Value).Pointer, height - 1, reached);
        }
    }

    private List<(int Key, string Value)> Scan()
    {
        var scan = new Scan([0x10]);
        scan.Enter(store!.Root);
        var all = new List<(int Key, string Value)>();
        while (scan.Next())
        {
            all.Add(((scan.Key[1] << 8) | scan.Key[2], Encoding.ASCII.GetString(scan.Value)));
        }

        return all;
    }

    private void Commit()
    {
        store!.Commit();
        committed[store.Generation] = new(model);
    }
}
