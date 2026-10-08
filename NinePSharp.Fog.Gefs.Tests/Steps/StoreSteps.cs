using System.Text;
using NinePSharp.Fog.Gefs.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class StoreSteps(StoreContext context)
{
    private const long B = Format.BlockSize;

    // Each step that changes the store, so a crash can be tried at every write of the next commit.
    private readonly List<Action> script = [];
    private Dictionary<string, SortedDictionary<int, string>> models = [];
    private Dictionary<long, Dictionary<string, SortedDictionary<int, string>>> committed = [];
    private long blocks;
    private int arenas;
    private int version;
    private string? failure;
    private long killer;
    private List<long> killed = [];

    [Given(@"^a device of (\d+) blocks reamed with (\d+) arenas$")]
    public void GivenReamed(long count, int arenaCount)
    {
        (blocks, arenas) = (count, arenaCount);
        Ream();
    }

    [Then(@"^both superblocks name (\d+) arenas and a snapshot tree (\d+) high, committed at generation (\d+)$")]
    public void ThenSuperblocks(int count, int height, long gen)
    {
        Superblock first = Superblock.Read(context.Device!.Block(0));
        Assert.Equal(context.Device.Block(0), context.Device.Block((context.Device.Size / B) - 1));
        Assert.Equal((count, height, gen, Format.BlockSize, Format.BufferSpace), (first.Arenas.Length, first.Snap.Height, first.SyncGen, first.BlockSize, first.BufferSpace));
        Assert.Equal(context.Store!.Allocator.Arenas.Select(a => a.Pointer.Addr), first.Arenas.Select(a => a.Addr));
    }

    [When("the device is reopened")]
    public void WhenReopened() => Do(WhenOpened);

    [When("the device is opened")]
    public void WhenOpened()
    {
        failure = null;
        try
        {
            context.Store = Store.Open(context.Device!);
            models = Clone(committed[context.Store.Generation]);
        }
        catch (GefsException error)
        {
            failure = error.Message;
        }
    }

    [Then(@"^the store is at generation (\d+) and main holds (keys .*|nothing)$")]
    public void ThenAtGeneration(long gen, string keys)
    {
        Assert.Null(failure);
        Assert.Equal(gen, context.Store!.Generation);
        ThenHolds("main", keys);
    }

    [Then(@"^opening fails with ""(.*)""$")]
    public void ThenOpeningFails(string message) => Assert.Equal(message, failure);

    [When(@"^(\w+) is given keys ((?:(?! and ).)*)$")]
    public void WhenGiven(string label, string keys) => Do(() => Give(label, keys));

    [When(@"^(\w+) is given keys (.*) and the store commits$")]
    public void WhenGivenAndCommitted(string label, string keys) => Do(() =>
    {
        Give(label, keys);
        Commit();
    });

    [When(@"^(\w+) has keys ((?:(?! and ).)*) deleted$")]
    public void WhenDeleted(string label, string keys) => Do(() => Remove(label, keys));

    [When(@"^(\w+) has keys (.*) deleted and the store commits$")]
    public void WhenDeletedAndCommitted(string label, string keys) => Do(() =>
    {
        Remove(label, keys);
        Commit();
    });

    [When(@"^(\w+) is given key (\d+) (\d+) times, the store committing after every (\d+)$")]
    public void WhenUpdated(string label, int n, int times, int every) => Do(() =>
    {
        for (int i = 1; i <= times; i++)
        {
            Give(label, $"{n} to {n}");
            if (i % every == 0)
            {
                Commit();
            }
        }
    });

    [When(@"^(\w+) is snapshotted as (\w+)$")]
    public void WhenSnapshotted(string label, string name) => Do(() => Tag(label, name, false));

    [When(@"^(\w+) is forked as (\w+)$")]
    public void WhenForked(string label, string name) => Do(() => Tag(label, name, true));

    [When(@"^(\w+) is unmounted$")]
    public void WhenUnmounted(string label) => Do(() => context.Store!.Unmount(label));

    [When(@"^(\w+) is deleted$")]
    public void WhenRemoved(string label) => Do(() => Delete(label));

    [When(@"^(\w+) is deleted and the store commits$")]
    public void WhenRemovedAndCommitted(string label) => Do(() =>
    {
        Delete(label);
        Commit();
    });

    [When(@"^(\w+) is snapshotted as a name of (\d+) bytes$")]
    public void WhenSnapshottedLong(string label, int length) => Do(() => Tag(label, new string('n', length), false));

    [Then(@"^the name of (\d+) bytes holds nothing$")]
    public void ThenLongHolds(int length) => ThenHolds(new string('n', length), "nothing");

    [Then(@"^snapshotting (\w+) as a name of (\d+) bytes fails with ""(.*)""$")]
    public void ThenLongFails(string label, int length, string message) => ThenTagFails("snapshotting", label, new string('n', length), message);

    [When(@"^(\w+) is given a data block for file (\d+) and the store commits$")]
    public void WhenGivenData(string label, long file) => Do(() =>
    {
        Tree t = context.Store!.Mount(label);
        Blk data = context.Store.Allocator.New(BlockType.Data, t.Gen);
        context.Store.Allocator.Enqueue(data);
        var key = new byte[17];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(1), file);
        var value = new byte[Format.PointerSize];
        data.Pointer.Write(value);
        t.Upsert(new Message(MessageOp.Insert, key, value));
        Commit();
    });

    [Then(@"^giving (\w+) key (\d+) fails with ""(.*)""$")]
    public void ThenGiveFails(string label, int n, string message)
        => Assert.Equal(message, Assert.Throws<GefsException>(() => context.Store!.Mount(label).Upsert(new Message(MessageOp.Insert, Key(n), [1]))).Message);

    [Then("a commit without changes leaves main's snapshot where it was")]
    public void ThenUnchangedCommit()
    {
        long gen = context.Store!.FindLabel("main")!.Value.Gen;
        Commit();
        Assert.Equal(gen, context.Store.FindLabel("main")!.Value.Gen);
    }

    [Then(@"^(\w+)'s snapshot has no predecessor, no successor, (no base|base \w+), (\d+) labels? and (\d+) forks?$")]
    public void ThenEntry(string label, string from, int labels, int forks)
    {
        TreeEntry e = context.Store!.Snapshot(context.Store.FindLabel(label)!.Value.Gen);
        long expected = from == "no base" ? -1 : context.Store.FindLabel(from.Split(' ')[1])!.Value.Gen;
        Assert.Equal((-1L, -1L, expected, labels, forks), (e.Pred, e.Succ, e.Base, e.Labels, e.Refs));
    }

    [Then(@"^(\w+)'s snapshot follows the one (\w+) was forked from$")]
    public void ThenFollowsBase(string label, string fork)
        => Assert.Equal(context.Store!.Snapshot(context.Store.FindLabel(fork)!.Value.Gen).Base, context.Store.Snapshot(context.Store.FindLabel(label)!.Value.Gen).Pred);

    [When(@"^a label (\w+) is made naming generation (\d+)$")]
    public void WhenGhost(string label, long gen)
    {
        var value = new byte[13];
        Keys.Snap(gen).CopyTo(value, 0);
        context.Store!.Snaps.Upsert(new Message(MessageOp.Insert, Keys.Label(label), value));
    }

    [When(@"^main's tree lists (\d+) blocks born in generation (\d+) as freed, and the store commits$")]
    public void WhenKills(int count, long birth)
    {
        Tree t = context.Store!.Mount("main");
        killer = t.Gen;
        killed = [.. Enumerable.Range(1, count).Select(i => i * B)];
        foreach (long addr in killed)
        {
            context.Store.Kill(t, new Bptr(addr, default, birth));
        }

        Commit();
    }

    [Then("no deadlist is open")]
    public void ThenNoneOpen() => Assert.Equal(0, context.Store!.OpenDeadlists);

    [Then(@"^main's deadlist for generation (\d+) lists those (\d+) blocks in (\d+) blocks$")]
    public void ThenDeadlist(long birth, int count, int blocks)
    {
        Assert.Equal(count, killed.Count);
        Assert.Equal(killed, context.Store!.DeadlistContents(killer, birth).Order());
        Assert.Equal(blocks, context.Store.DeadlistBlocks(killer, birth));
    }

    [Then(@"^(\w+) holds (keys .*|nothing)$")]
    public void ThenHolds(string label, string keys)
    {
        var expected = keys == "nothing" ? [] : Numbers(keys.Split(' ', 2)[1]).ToList();
        var held = Contents(context.Store!.Mount(label));
        Assert.Equal(expected, held.Select(kv => kv.Key));
        Assert.Equal(models[label].Select(kv => (kv.Key, kv.Value)), held);
    }

    [Then(@"^(\w+) does not exist$")]
    public void ThenGone(string label) => Assert.Null(context.Store!.FindLabel(label));

    [Then(@"^(\w+)'s snapshot has no predecessor$")]
    public void ThenNoPredecessor(string label) => Assert.Equal(-1, context.Store!.Snapshot(context.Store.FindLabel(label)!.Value.Gen).Pred);

    [Then(@"^(\w+)'s snapshot follows (\w+)'s$")]
    public void ThenFollows(string label, string older)
        => Assert.Equal(context.Store!.FindLabel(older)!.Value.Gen, context.Store.Snapshot(context.Store.FindLabel(label)!.Value.Gen).Pred);

    [Then(@"^(snapshotting|forking) (\w+) as (\w+) fails with ""(.*)""$")]
    public void ThenTagFails(string how, string label, string name, string message)
        => Assert.Equal(message, Assert.Throws<GefsException>(() => context.Store!.Tag(label, name, how == "forking")).Message);

    [Then(@"^deleting (\w+) fails with ""(.*)""$")]
    public void ThenDeleteFails(string label, string message) => Assert.Equal(message, Assert.Throws<GefsException>(() => context.Store!.Delete(label)).Message);

    [Then(@"^mounting (\w+) fails with ""(.*)""$")]
    public void ThenMountFails(string label, string message) => Assert.Equal(message, Assert.Throws<GefsException>(() => context.Store!.Mount(label)).Message);

    [Then(@"^unmounting (\w+) fails with ""(.*)""$")]
    public void ThenUnmountFails(string label, string message) => Assert.Equal(message, Assert.Throws<GefsException>(() => context.Store!.Unmount(label)).Message);

    [When(@"^(\d+) qids are taken and the store commits$")]
    public void WhenQids(int count)
    {
        for (int i = 0; i < count; i++)
        {
            context.Store!.NewQid();
        }

        Commit();
    }

    [Then(@"^the next qid is (\d+)$")]
    public void ThenNextQid(long qid) => Assert.Equal(qid, context.Store!.NewQid());

    [Then(@"^main writes in generation (\d+)$")]
    public void ThenWritesIn(long gen) => Assert.Equal(gen, context.Store!.Mount("main").Gen);

    [Then("each arena's log is 1 block long")]
    public void ThenLogsCompressed() => Assert.All(context.Store!.Allocator.Arenas, a => Assert.Single(a.LogAddresses()));

    [When(@"^the (first|backup|both) superblocks? (?:is|are) (damaged|written by another version)$")]
    public void WhenSuperblockChanged(string which, string change)
    {
        long last = (context.Device!.Size / B) - 1;
        foreach (long n in which switch { "first" => [0L], "backup" => [last], _ => new[] { 0L, last } })
        {
            if (change == "damaged")
            {
                context.Device.Damage(n);
            }
            else
            {
                byte[] block = context.Device.Block(n);
                "gefs9.00"u8.CopyTo(block);
                context.Device.Put(n, block);
            }
        }
    }

    [When(@"^both superblocks are rewritten claiming (blocks|buffers) of (\d+) bytes$")]
    public void WhenRewritten(string what, int size)
    {
        long last = (context.Device!.Size / B) - 1;
        Superblock sb = Superblock.Read(context.Device.Block(0));
        sb = what == "blocks" ? sb with { BlockSize = size } : sb with { BufferSpace = size };
        var block = new byte[B];
        sb.Write(block);
        context.Device.Put(0, block);
        context.Device.Put(last, block);
    }

    [When("the device's record is cleared and the store commits")]
    public void WhenTracedCommit()
    {
        context.Device!.Trace.Clear();
        Commit();
    }

    [Then(@"^the device recorded: (.*)$")]
    public void ThenRecorded(string events) => Assert.Equal(events.Split(", "), context.Device!.Trace);

    [Then(@"^a crash after any number of the next commit's writes opens as generation (\d+) or (\d+), as committed, every block held, in a log or free$")]
    public void ThenCrashes(long before, long after)
    {
        var seen = new HashSet<long>();
        for (int k = 0; ; k++)
        {
            Ream();
            foreach (Action step in script)
            {
                step();
            }

            int writes = context.Device!.Writes;
            context.Device.CrashAfter(k);
            Commit();
            context.Device.Restart();
            bool whole = context.Device.Writes - writes <= k;
            WhenOpened();
            Assert.Null(failure);
            Assert.Contains(context.Store!.Generation, (long[])[before, after]);
            seen.Add(context.Store.Generation);
            foreach (string label in models.Keys)
            {
                Assert.Equal(models[label].Select(kv => (kv.Key, kv.Value)), Contents(context.Store.Mount(label)));
            }

            ThenAccounted();
            if (whole)
            {
                break;
            }
        }

        Assert.Equal([before, after], seen.Order());
    }

    [Then("every block of the device is held, in a log or free")]
    public void ThenAccounted() => Accounting.Check(context.Store!);

    private static byte[] Key(int n) => [0x10, (byte)(n >> 8), (byte)n];

    private static IEnumerable<int> Numbers(string text)
    {
        string[] ends = text.Split(" to ");
        return Enumerable.Range(int.Parse(ends[0]), int.Parse(ends[^1]) - int.Parse(ends[0]) + 1);
    }

    private static Dictionary<string, SortedDictionary<int, string>> Clone(Dictionary<string, SortedDictionary<int, string>> from)
        => from.ToDictionary(kv => kv.Key, kv => new SortedDictionary<int, string>(kv.Value));

    private static List<(int Key, string Value)> Contents(Tree t)
        => [.. Accounting.Scan(t, [0x10]).Select(kv => ((kv.Key[1] << 8) | kv.Key[2], Encoding.ASCII.GetString(kv.Value)))];

    private void Ream()
    {
        context.Device = new MemoryDevice(blocks);
        context.Store = Store.Ream(context.Device, arenas);
        models = new() { ["main"] = [], ["empty"] = [] };
        committed = new() { [context.Store.Generation] = Clone(models) };
    }

    private void Do(Action step)
    {
        script.Add(step);
        step();
    }

    private void Give(string label, string keys)
    {
        Tree t = context.Store!.Mount(label);
        foreach (int n in Numbers(keys))
        {
            string value = $"{label}:{n}:{++version}:" + new string('v', 400 - $"{label}:{n}:{version}:".Length);
            t.Upsert(new Message(MessageOp.Insert, Key(n), Encoding.ASCII.GetBytes(value)));
            models[label][n] = value;
        }
    }

    private void Remove(string label, string keys)
    {
        Tree t = context.Store!.Mount(label);
        foreach (int n in Numbers(keys))
        {
            t.Upsert(new Message(MessageOp.Delete, Key(n), []));
            models[label].Remove(n);
        }
    }

    private void Tag(string label, string name, bool mutable)
    {
        context.Store!.Tag(label, name, mutable);
        models[name] = new SortedDictionary<int, string>(models[label]);
    }

    private void Delete(string label)
    {
        context.Store!.Delete(label);
        models.Remove(label);
    }

    private void Commit()
    {
        context.Store!.Commit();
        committed[context.Store.Generation] = Clone(models);
    }
}
