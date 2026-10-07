using System.Buffers.Binary;
using System.Text;
using NinePSharp.Fog.Gefs.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class TreeSteps
{
    private static readonly Comparer<byte[]> KeyOrder = Comparer<byte[]>.Create((a, b) => Keys.Compare(a, b));

    private readonly SortedDictionary<byte[], byte[]> model = new(KeyOrder);
    private readonly HashSet<byte[]> touched = new(ByteArrayComparer.Instance);
    private readonly Dictionary<string, Bptr> data = [];
    private MemoryStore store = new();
    private Tree? tree;
    private Scan? scan;
    private Bptr rootBefore;
    private List<(byte[] Key, Bptr Child)> childrenBefore = [];
    private int allocationsBefore;
    private int freedBefore;
    private int version;
    private string? failure;

    [Given("an empty tree")]
    public void GivenEmptyTree()
    {
        store = new MemoryStore();
        tree = Tree.Create(store);
    }

    [Given(@"^a tree 2 high whose root points at a leaf of keys (\d+) to (\d+) and a leaf of keys (\d+) to (\d+)$")]
    public void GivenTwoLeaves(int a, int b, int c, int d)
    {
        store = new MemoryStore();
        Blk root = store.New(BlockType.Pivot);
        foreach (var (first, last) in new[] { (a, b), (c, d) })
        {
            Blk leaf = store.New(BlockType.Leaf);
            for (int n = first; n <= last; n++)
            {
                byte[] value = NextValue();
                leaf.SetValue(Key(n), value);
                Remember(Key(n), value);
            }

            store.Enqueue(leaf);
            root.SetPointer(Key(first), leaf.Pointer, leaf.Fill);
        }

        store.Enqueue(root);
        tree = new Tree(store, root.Pointer, 2);
    }

    [Given(@"^its root buffers (.*)$")]
    public void GivenRootBuffers(string messages)
    {
        Blk old = store.Get(tree!.Root);
        Blk root = store.Duplicate(old);
        foreach (string item in Items(messages))
        {
            string[] words = item.Split(' ');
            Message m = Parse(words[1], Key(int.Parse(words[^1])));
            root.SetMessage(m);
            Apply(m);
        }

        store.Enqueue(root);
        store.Free(old.Pointer);
        tree = new Tree(store, root.Pointer, 2);
    }

    [Given(@"^a leaf root recorded as (\d+) high$")]
    public void GivenTallLeaf(int height)
    {
        store = new MemoryStore();
        Blk leaf = store.New(BlockType.Leaf);
        store.Enqueue(leaf);
        tree = new Tree(store, leaf.Pointer, height);
    }

    [Given(@"^data blocks (.*)$")]
    public void GivenDataBlocks(string names)
    {
        foreach (string name in Items(names))
        {
            Blk b = store.New(BlockType.Data);
            store.Enqueue(b);
            data[name] = b.Pointer;
        }
    }

    [When(@"^keys? (.*) (?:is|are) inserted one at a time$")]
    public void WhenInsertedEach(string keys)
    {
        foreach (int n in Numbers(keys))
        {
            Upsert(new Message(MessageOp.Insert, Key(n), NextValue()));
        }
    }

    [When(@"^keys? (.*) (?:is|are) inserted in one upsert$")]
    public void WhenInsertedTogether(string keys)
        => Upsert([.. Numbers(keys).Select(n => new Message(MessageOp.Insert, Key(n), NextValue()))]);

    [When(@"^key (\d+) is inserted$")]
    public void WhenInserted(int n) => Upsert(new Message(MessageOp.Insert, Key(n), NextValue()));

    [When(@"^key (\d+) is deleted$")]
    public void WhenDeleted(int n) => Upsert(new Message(MessageOp.Delete, Key(n), []));

    [When(@"^key 1 is given (.*) and then (.*) in one upsert$")]
    public void WhenGivenTwo(string first, string second) => Upsert(Named(first), Named(second));

    [When(@"^a key of (\d+) bytes is inserted with a value of (\d+) bytes$")]
    public void WhenSized(int key, int value)
    {
        var k = new byte[key];
        k[0] = 0x10;
        Upsert(new Message(MessageOp.Insert, k, new byte[value]));
    }

    [When(@"^it is given (\d+) random upserts of up to (\d+) messages over (\d+) keys from seed (\d+), checked every (\d+)$")]
    public void WhenRandom(int count, int most, int keys, int seed, int every)
    {
        var random = new Random(seed);
        byte[][] universe = [.. Enumerable.Range(0, keys).Select(_ => RandomKey(random))];
        for (int i = 1; i <= count; i++)
        {
            var batch = new List<Message>();
            var present = new HashSet<byte[]>(model.Keys, ByteArrayComparer.Instance);
            for (int n = random.Next(1, most + 1); n > 0; n--)
            {
                byte[] key = universe[random.Next(universe.Length)];
                int roll = random.Next(100);
                Message m = present.Contains(key)
                    ? roll < 50 ? new Message(MessageOp.Delete, key, []) : roll < 90 ? Insert(key, random) : new Message(MessageOp.Clobber, key, [])
                    : roll < 85 ? Insert(key, random) : new Message(roll < 92 ? MessageOp.Clobber : MessageOp.ClearBlock, key, []);
                if (m.Op == MessageOp.Insert)
                {
                    present.Add(key);
                }
                else
                {
                    present.Remove(key);
                }

                batch.Add(m);
            }

            Upsert([.. batch]);
            Assert.Null(failure);
            if (i % every == 0)
            {
                ThenEveryKeyLooksUp();
                ThenWellFormed();
            }
        }
    }

    [When(@"^the data entry for file (\d+) at offset (\d+) is inserted pointing at (\w+)$")]
    public void WhenDataInserted(long file, long offset, string block) => Upsert(new Message(MessageOp.Insert, DataKey(file, offset), Pointer(block)));

    [When(@"^the data entry for file (\d+) at offset (\d+) is inserted pointing at (\w+) and then at (\w+) in one upsert$")]
    public void WhenDataInsertedTwice(long file, long offset, string first, string second)
        => Upsert(new Message(MessageOp.Insert, DataKey(file, offset), Pointer(first)), new Message(MessageOp.Insert, DataKey(file, offset), Pointer(second)));

    [When(@"^the data entry for file (\d+) at offset (\d+) is cleared$")]
    public void WhenDataCleared(long file, long offset) => Upsert(new Message(MessageOp.ClearBlock, DataKey(file, offset), []));

    [When(@"^the data entry for file (\d+) at offset (\d+) is clobbered and its owner frees block (\w+)$")]
    public void WhenDataClobbered(long file, long offset, string block)
    {
        Upsert(new Message(MessageOp.Clobber, DataKey(file, offset), []));
        store.Free(data[block]);
    }

    [When(@"^a scan of the whole tree gives (\d+) entries and is left$")]
    public void WhenScanLeft(int count)
    {
        scan = new Scan([0x10]);
        scan.Enter(tree!);
        for (int i = 0; i < count; i++)
        {
            Assert.True(scan.Next());
        }

        scan.Exit();
    }

    [When("the scan is re-entered")]
    public void WhenScanReentered() => scan!.Enter(tree!);

    [Then(@"^it gives keys (.*), and then nothing$")]
    public void ThenScanGives(string keys)
    {
        foreach (int n in Numbers(keys))
        {
            Assert.True(scan!.Next());
            Assert.Equal(Hex(Key(n)), Hex(scan.Key));
            Assert.Equal(Hex(model[Key(n)]), Hex(scan.Value));
        }

        Assert.False(scan!.Next());
        scan.Exit();
    }

    [Then(@"^the tree is (\d+) high and its root is a leaf of (\d+) entries$")]
    public void ThenLeafRoot(int height, int entries)
    {
        Assert.Equal(height, tree!.Height);
        Blk root = store.Get(tree.Root);
        Assert.Equal((BlockType.Leaf, entries), (root.Type, root.ValueCount));
    }

    [Then(@"^the tree is (\d+) high$")]
    public void ThenHeight(int height) => Assert.Equal(height, tree!.Height);

    [Then(@"^it has grown to (\d+) high$")]
    public void ThenGrown(int height) => Assert.True(tree!.Height >= height, $"the tree is only {tree.Height} high");

    [Then(@"^its root points at a leaf of (\d+) entries from key (\d+) and a leaf of (\d+) entries from key (\d+)$")]
    public void ThenRootPoints(int left, int leftKey, int right, int rightKey)
    {
        var children = Children();
        Assert.Equal(
            [(Hex(Key(leftKey)), BlockType.Leaf, left), (Hex(Key(rightKey)), BlockType.Leaf, right)],
            children.Select(c => (Hex(c.Key), store.Get(c.Child).Type, store.Get(c.Child).ValueCount)));
    }

    [Then(@"^its root points at the same leaf from key (\d+) and a leaf of (\d+) entries from key (\d+)$")]
    public void ThenRootKeeps(int leftKey, int right, int rightKey)
    {
        var children = Children();
        Assert.Equal(2, children.Count);
        Assert.Equal((Hex(Key(leftKey)), childrenBefore[0].Child), (Hex(children[0].Key), children[0].Child));
        Assert.Equal((Hex(Key(rightKey)), right), (Hex(children[1].Key), store.Get(children[1].Child).ValueCount));
    }

    [Then(@"^its root buffers (no messages|messages for keys? .*)$")]
    public void ThenRootBuffers(string messages)
    {
        Blk root = store.Get(tree!.Root);
        Assert.Equal(
            messages == "no messages" ? [] : Numbers(messages.Split(' ', 4)[^1]).Select(n => Hex(Key(n))),
            Enumerable.Range(0, root.MessageCount).Select(i => Hex(root.GetMessage(i).Key)));
    }

    [Then("only the root was rewritten, and only the old root was freed")]
    public void ThenOnlyRoot()
    {
        Assert.Equal(1, store.Allocations - allocationsBefore);
        Assert.Equal([rootBefore.Addr], store.Freed.Skip(freedBefore));
        Assert.Equal(childrenBefore.Select(c => c.Child), Children().Select(c => c.Child));
    }

    [Then("every key looks up as it was last given and a scan gives them in key order")]
    public void ThenEveryKeyLooksUp()
    {
        foreach (byte[] key in touched)
        {
            Assert.Equal(model.TryGetValue(key, out byte[]? value) ? Hex(value) : null, tree!.Lookup(key) is { } found ? Hex(found) : null);
        }

        Assert.Equal(model.Select(kv => (Hex(kv.Key), Hex(kv.Value))), ScanAll([0x10]).Select(kv => (Hex(kv.Key), Hex(kv.Value))));
    }

    [Then("every block of it is well formed, and every block it no longer uses was freed")]
    public void ThenWellFormed() => Assert.Equal(store.Live.Order(), TreeCheck.Reachable(store, tree!).Order());

    [Then(@"^looking up key (\d+) (finds value \w+|finds nothing)$")]
    public void ThenLookup(int n, string result)
        => Assert.Equal(result == "finds nothing" ? null : result.Split(' ')[^1], tree!.Lookup(Key(n)) is { } found ? Encoding.ASCII.GetString(found) : null);

    [Then(@"^looking up key (\d+) fails with ""(.*)""$")]
    public void ThenLookupFails(int n, string message) => Assert.Equal(message, Assert.Throws<GefsException>(() => tree!.Lookup(Key(n))).Message);

    [Then("a scan of the whole tree gives nothing")]
    public void ThenScanNothing() => Assert.Empty(ScanAll([0x10]));

    [Then(@"^a scan of keys beginning ([0-9a-f]+) gives (key \d+|nothing)$")]
    public void ThenPrefixScan(string prefix, string result)
        => Assert.Equal(
            result == "nothing" ? [] : [Hex(Key(int.Parse(result.Split(' ')[^1])))],
            ScanAll(Convert.FromHexString(prefix)).Select(kv => Hex(kv.Key)));

    [Then(@"^block (\w+) is still in use$")]
    public void ThenInUse(string block) => Assert.Contains(data[block].Addr, store.Live);

    [Then(@"^block (\w+) was freed$")]
    public void ThenFreed(string block) => Assert.Contains(data[block].Addr, store.Freed);

    [Then(@"^block (\w+) was freed and block (\w+) is still in use$")]
    public void ThenFreedAndInUse(string freed, string kept)
    {
        ThenFreed(freed);
        ThenInUse(kept);
    }

    [Then(@"^the data entry for file (\d+) at offset (\d+) looks up as nothing$")]
    public void ThenDataMissing(long file, long offset) => Assert.Null(tree!.Lookup(DataKey(file, offset)));

    [Then(@"^the upsert (succeeds|fails with "".*"")$")]
    public void ThenUpsert(string result) => Assert.Equal(result == "succeeds" ? null : result.Split('"')[1], failure);

    private static byte[] Key(int n) => [0x10, (byte)(n >> 8), (byte)n];

    private static byte[] DataKey(long file, long offset)
    {
        var key = new byte[1 + 8 + 8];
        key[0] = (byte)KeyType.Data;
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(1), file);
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(9), offset);
        return key;
    }

    private static byte[] RandomKey(Random random)
    {
        var key = new byte[random.Next(150, Format.MaxEntry + 1)];
        random.NextBytes(key);
        key[0] = 0x10;
        return key;
    }

    private static Message Insert(byte[] key, Random random)
    {
        var value = new byte[random.Next(0, Format.MaxInline + 1)];
        random.NextBytes(value);
        return new Message(MessageOp.Insert, key, value);
    }

    private static Message Parse(string op, byte[] key) => op switch
    {
        "insert" => new Message(MessageOp.Insert, key, []),
        "delete" => new Message(MessageOp.Delete, key, []),
        _ => new Message(MessageOp.Clobber, key, []),
    };

    private static Message Named(string text)
    {
        string[] words = text.Split(' ');
        return words[1] == "insert"
            ? new Message(MessageOp.Insert, Key(1), Encoding.ASCII.GetBytes(words[^1]))
            : Parse(words[1], Key(1));
    }

    private static IEnumerable<string> Items(string text) => text.Replace(" and ", ", ", StringComparison.Ordinal).Split(", ");

    private static IEnumerable<int> Numbers(string text)
    {
        foreach (string item in Items(text))
        {
            string[] range = item.Split(" to ");
            int first = int.Parse(range[0]);
            int last = int.Parse(range[^1]);
            for (int n = first; n <= last; n++)
            {
                yield return n;
            }
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private byte[] Pointer(string block)
    {
        var value = new byte[Format.PointerSize];
        data[block].Write(value);
        return value;
    }

    private byte[] NextValue()
    {
        var value = new byte[500];
        BinaryPrimitives.WriteInt32BigEndian(value, ++version);
        return value;
    }

    private void Upsert(params Message[] messages)
    {
        failure = null;
        rootBefore = tree!.Root;
        childrenBefore = Children();
        allocationsBefore = store.Allocations;
        freedBefore = store.Freed.Count;
        try
        {
            tree.Upsert(messages);
        }
        catch (GefsException error)
        {
            failure = error.Message;
            return;
        }

        foreach (Message m in messages)
        {
            Apply(m);
        }
    }

    private void Apply(Message m)
    {
        touched.Add(m.Key);
        if (m.Op == MessageOp.Insert)
        {
            model[m.Key] = m.Value;
        }
        else
        {
            model.Remove(m.Key);
        }
    }

    private void Remember(byte[] key, byte[] value)
    {
        touched.Add(key);
        model[key] = value;
    }

    private List<(byte[] Key, Bptr Child)> Children()
    {
        Blk root = store.Get(tree!.Root);
        return root.Type == BlockType.Leaf
            ? []
            : [.. Enumerable.Range(0, root.ValueCount).Select(root.GetValue).Select(kv => (kv.Key, Blk.GetPointer(kv.Value).Pointer))];
    }

    private List<(byte[] Key, byte[] Value)> ScanAll(byte[] prefix)
    {
        var s = new Scan(prefix);
        s.Enter(tree!);
        var all = new List<(byte[] Key, byte[] Value)>();
        while (s.Next())
        {
            all.Add((s.Key, s.Value));
        }

        s.Exit();
        return all;
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = default(HashCode);
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
