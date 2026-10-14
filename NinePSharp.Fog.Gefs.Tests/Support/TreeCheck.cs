using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Support;

// Walks a tree from its root, checking each block against its parent, and collects every block it
// reaches, including the data blocks its entries and buffered messages point at.
internal static class TreeCheck
{
    public static HashSet<long> Reachable(MemoryStore store, Tree tree)
    {
        var reached = new HashSet<long>();
        Walk(store, tree.Root, tree.Height, null, null, reached, true);
        return reached;
    }

    private static void Walk(MemoryStore store, Bptr bp, int height, byte[]? lo, byte[]? hi, HashSet<long> reached, bool root)
    {
        Assert.True(reached.Add(bp.Addr), $"block {bp.Addr} is reached twice");
        Blk b = store.Get(bp);
        Assert.Equal(height == 1 ? BlockType.Leaf : BlockType.Pivot, b.Type);
        var keys = Enumerable.Range(0, b.ValueCount).Select(b.GetValue).ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            Assert.True(i == 0 || Keys.Compare(keys[i - 1].Key, keys[i].Key) < 0, "values are out of order");
            Within(keys[i].Key, lo, hi);
        }

        if (b.Type == BlockType.Leaf)
        {
            foreach (var (key, value) in keys)
            {
                Data(key, value, reached);
            }

            return;
        }

        Assert.True(keys.Count >= (root ? 2 : 1), "a pivot has too few children");
        byte[]? last = null;
        for (int i = 0; i < b.MessageCount; i++)
        {
            Message m = b.GetMessage(i);
            Assert.True(last is null || Keys.Compare(last, m.Key) <= 0, "buffered messages are out of order");
            Within(m.Key, lo, hi);
            if (m.Op == MessageOp.Insert)
            {
                Data(m.Key, m.Value, reached);
            }

            last = m.Key;
        }

        for (int i = 0; i < keys.Count; i++)
        {
            var (child, fill) = Blk.GetPointer(keys[i].Value);
            Assert.Equal(store.Get(child).Fill, fill);
            byte[]? below = i == 0 ? lo : keys[i].Key;
            byte[]? above = i + 1 < keys.Count ? keys[i + 1].Key : hi;
            Walk(store, child, height - 1, below, above, reached, false);
        }
    }

    private static void Within(byte[] key, byte[]? lo, byte[]? hi)
    {
        Assert.True(lo is null || Keys.Compare(lo, key) <= 0, "a key sorts before its block's range");
        Assert.True(hi is null || Keys.Compare(key, hi) < 0, "a key sorts after its block's range");
    }

    private static void Data(byte[] key, byte[] value, HashSet<long> reached)
    {
        if (key[0] == (byte)KeyType.Data)
        {
            reached.Add(Bptr.Read(value).Addr);
        }
    }
}
