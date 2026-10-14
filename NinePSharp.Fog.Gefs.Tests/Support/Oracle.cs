using System.Buffers.Binary;
using System.Globalization;

namespace NinePSharp.Fog.Gefs.Tests.Support;

// The upserts tools/gefs-oracle/harness.c makes from a seed, drawn the same way, and the digest of a
// tree's shape it prints after each.
internal sealed class Oracle
{
    private readonly int count;
    private readonly int most;
    private readonly int grow;
    private readonly int maxval;
    private readonly byte[][] keys;
    private ulong rng;

    private Oracle(int[] args)
    {
        rng = (ulong)args[0];
        count = args[1];
        maxval = args[5];
        most = args[6];
        grow = args[7];
        keys = new byte[args[2]][];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new byte[Between(args[3], args[4])];
            for (int j = 0; j < keys[i].Length; j++)
            {
                keys[i][j] = (byte)Next();
            }

            keys[i][0] = 0x10;
        }
    }

    // Step, height and digest after every tenth upsert, as the harness printed them.
    public List<(int Step, int Height, ulong Digest)> Expected { get; } = [];

    public static Oracle Load(string run)
    {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", run + ".txt"));
        string header = lines[0];
        var oracle = new Oracle([.. header[(header.IndexOf("harness ", StringComparison.Ordinal) + 8)..].Split(' ').Select(int.Parse)]);
        foreach (string line in lines.Skip(1))
        {
            string[] fields = line.Split(' ');
            oracle.Expected.Add((int.Parse(fields[0]), int.Parse(fields[1]), ulong.Parse(fields[2], NumberStyles.HexNumber)));
        }

        return oracle;
    }

    public static ulong Digest(BlockStore store, Tree tree)
    {
        ulong digest = 0xcbf29ce484222325;
        Feed(ref digest, [(byte)tree.Height]);
        Walk(store, tree.Root, ref digest);
        return digest;
    }

    public IEnumerable<(int Step, Message[] Batch)> Upserts()
    {
        var present = new bool[keys.Length];
        for (int step = 1; step <= count; step++)
        {
            bool growing = step <= grow;
            var batch = new Message[Between(1, most)];
            for (int i = 0; i < batch.Length; i++)
            {
                int k = Between(0, keys.Length - 1);
                int roll = Between(0, 99);
                MessageOp op = present[k]
                    ? roll < (growing ? 30 : 90) ? MessageOp.Delete : roll < (growing ? 90 : 97) ? MessageOp.Insert : MessageOp.Clobber
                    : roll < (growing ? 85 : 5) ? MessageOp.Insert : roll < (growing ? 92 : 60) ? MessageOp.Clobber : MessageOp.ClearBlock;
                byte[] value = [];
                if (op == MessageOp.Insert)
                {
                    value = new byte[Between(0, maxval)];
                    for (int j = 0; j < value.Length; j++)
                    {
                        value[j] = (byte)Next();
                    }
                }

                present[k] = op == MessageOp.Insert;
                batch[i] = new Message(op, keys[k], value);
            }

            yield return (step, batch);
        }
    }

    private static void Walk(BlockStore store, Bptr bp, ref ulong digest)
    {
        Blk b = store.Get(bp);
        Feed(ref digest, [(byte)(b.Type == BlockType.Leaf ? 'L' : 'P')]);
        Feed16(ref digest, b.ValueCount);
        Feed16(ref digest, b.MessageCount);
        for (int i = 0; i < b.ValueCount; i++)
        {
            var (key, value) = b.GetValue(i);
            Feed16(ref digest, key.Length);
            Feed(ref digest, key);
            if (b.Type == BlockType.Leaf)
            {
                Feed16(ref digest, value.Length);
                Feed(ref digest, value);
            }
            else
            {
                var (child, fill) = Blk.GetPointer(value);
                Feed16(ref digest, fill);
                Walk(store, child, ref digest);
            }
        }

        for (int i = 0; i < b.MessageCount; i++)
        {
            Message m = b.GetMessage(i);
            Feed(ref digest, [(byte)m.Op]);
            Feed16(ref digest, m.Key.Length);
            Feed(ref digest, m.Key);
            Feed16(ref digest, m.Value.Length);
            Feed(ref digest, m.Value);
        }
    }

    private static void Feed16(ref ulong digest, int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        Feed(ref digest, b);
    }

    // FNV-1a
    private static void Feed(ref ulong digest, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            digest = (digest ^ b) * 0x100000001b3;
        }
    }

    // splitmix64
    private ulong Next()
    {
        ulong z = rng += 0x9e3779b97f4a7c15;
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9;
        z = (z ^ (z >> 27)) * 0x94d049bb133111eb;
        return z ^ (z >> 31);
    }

    private int Between(int lo, int hi) => lo + (int)(Next() % (ulong)(hi - lo + 1));
}
