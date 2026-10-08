using System.Buffers.Binary;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Support;

// Every block of the device in exactly one place: held by the snapshot tree, a snapshot's tree, the
// file data it names or a deadlist; in an arena's log; or free.
internal static class Accounting
{
    private const long B = Format.BlockSize;

    public static void Check(Store store)
    {
        var held = new HashSet<long>();
        Walk(store, store.Snaps.Root, store.Snaps.Height, held);
        foreach (var (_, value) in Scan(store.Snaps, [(byte)KeyType.Snap]))
        {
            TreeEntry e = TreeEntry.Read(value);
            Walk(store, e.Root, e.Height, held);
            foreach (var (_, named) in Scan(new Tree(store.Allocator, e.Root, e.Height), [(byte)KeyType.Data]))
            {
                held.Add(Bptr.Read(named).Addr);
            }
        }

        // A deadlist lists only blocks an older snapshot still holds; any other would be leaked.
        var inTrees = held.ToHashSet();
        foreach (var (key, value) in Scan(store.Snaps, [(byte)KeyType.Deadlist]))
        {
            // Each deadlist belongs to a snapshot, and lists only blocks born after its base.
            long birth = BinaryPrimitives.ReadInt64BigEndian(key.AsSpan(9));
            Assert.True(birth > store.Snapshot(BinaryPrimitives.ReadInt64BigEndian(key.AsSpan(1))).Base);
            for (Bptr at = Bptr.Read(value); at.Addr != -1;)
            {
                Blk b = store.Allocator.Get(at);
                Assert.True(held.Add(at.Addr), $"deadlist block {at.Addr} is also held elsewhere");
                for (int i = 0; i < b.LogSize; i += 8)
                {
                    long listed = BinaryPrimitives.ReadInt64BigEndian(b.Data[i..]);
                    Assert.True(inTrees.Contains(listed), $"deadlisted block {listed} is held by no snapshot");
                }

                at = b.LogNext;
            }
        }

        var logs = store.Allocator.Arenas.SelectMany(a => a.LogAddresses()).ToHashSet();
        var free = store.Allocator.Arenas.SelectMany(a => a.Free.SelectMany(r => Enumerable.Range(0, (int)(r.Length / B)).Select(k => r.Offset + (k * B)))).ToHashSet();
        var data = store.Allocator.Arenas.SelectMany(a => Enumerable.Range(0, (int)(a.Size / B)).Select(k => a.Start + (k * B))).ToHashSet();
        Assert.Empty(held.Intersect(free));
        Assert.Empty(held.Intersect(logs));
        Assert.Empty(logs.Intersect(free));
        Assert.Equal(data.Order(), held.Concat(logs).Concat(free).Order());
    }

    public static List<(byte[] Key, byte[] Value)> Scan(Tree t, byte[] prefix)
    {
        var scan = new Scan(prefix);
        scan.Enter(t);
        var all = new List<(byte[] Key, byte[] Value)>();
        while (scan.Next())
        {
            all.Add((scan.Key, scan.Value));
        }

        scan.Exit();
        return all;
    }

    private static void Walk(Store store, Bptr bp, int height, HashSet<long> held)
    {
        held.Add(bp.Addr);
        Blk b = store.Allocator.Get(bp);
        for (int i = 0; height > 1 && i < b.ValueCount; i++)
        {
            Walk(store, Blk.GetPointer(b.GetValue(i).Value).Pointer, height - 1, held);
        }
    }
}
