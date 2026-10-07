namespace NinePSharp.Fog.Gefs;

// btnewscan, btenter, btnext and btexit: the keys beginning with a prefix, in order, with buffered
// messages applied. A scan can be left and entered again after the tree changes, and carries on
// after the last key it gave.
internal sealed class Scan(ReadOnlySpan<byte> prefix)
{
    private readonly byte[] prefix = prefix.ToArray();
    private readonly Blk?[] blocks = new Blk?[Format.MaxHeight];
    private readonly int[] vi = new int[Format.MaxHeight];
    private readonly int[] bi = new int[Format.MaxHeight];
    private BlockStore? store;
    private int height;
    private bool first = true;
    private bool done;

    public byte[] Key { get; private set; } = prefix.ToArray();

    public byte[] Value { get; private set; } = [];

    // btenter: down from the root to the first key at or after the scan's place.
    public void Enter(Tree t)
    {
        if (done)
        {
            return;
        }

        store = t.Store;
        (Bptr bp, height) = t.GetRoot();
        Blk b = store.Get(bp);
        for (int i = 0; i < height; i++)
        {
            blocks[i] = b;
            vi[i] = b.BlockSearch(Key, out bool same);
            if (b.Type == BlockType.Pivot)
            {
                vi[i] = Math.Max(vi[i], 0);
                bi[i] = b.BufferSearch(Key, out same);
                if (bi[i] == -1)
                {
                    bi[i] = 0;
                }
                else if (!same || !first)
                {
                    // Past the messages for the key already given, or for the key before the place.
                    byte[] key = b.GetMessage(bi[i]).Key;
                    while (bi[i] < b.MessageCount && Keys.Compare(key, b.GetMessage(bi[i]).Key) == 0)
                    {
                        bi[i]++;
                    }
                }

                b = store.Get(Blk.GetPointer(b.GetValue(vi[i]).Value).Pointer);
            }
            else if (vi[i] == -1 || !same || !first)
            {
                vi[i]++;
            }
        }

        first = false;
    }

    // btnext: the least key among the leaf's next value and each pivot's next buffered message.
    public bool Next()
    {
        while (height > 0 && !done)
        {
            // Drop the levels with nothing left; the level above moves to its next child.
            int start = height;
            for (int i = height - 1; blocks[i] is not { } b || (vi[i] >= b.ValueCount && bi[i] >= b.MessageCount); i--)
            {
                if (i == 0)
                {
                    done = true;
                    return false;
                }

                (blocks[i], vi[i], bi[i]) = (null, 0, 0);
                vi[i - 1]++;
                start = i;
            }

            Message m;
            bool ok;
            int bufsrc = -1;
            Blk above = blocks[start - 1]!;
            if (vi[start - 1] < above.ValueCount)
            {
                for (int i = start; i < height; i++)
                {
                    blocks[i] = store!.Get(Blk.GetPointer(blocks[i - 1]!.GetValue(vi[i - 1]).Value).Pointer);
                }

                var (key, value) = blocks[height - 1]!.GetValue(vi[height - 1]);
                m = new Message(MessageOp.Insert, key, value);
                ok = true;
            }
            else
            {
                m = above.GetMessage(bi[start - 1]);
                ok = Starts(m);
                bufsrc = start - 1;
            }

            for (int i = height - 2; i >= 0; i--)
            {
                if (blocks[i] is { } b && bi[i] < b.MessageCount && b.GetMessage(bi[i]) is var n && Keys.Compare(n.Key, m.Key) < 0)
                {
                    ok = Starts(n);
                    bufsrc = i;
                    m = n;
                }
            }

            if (!m.Key.AsSpan().StartsWith(prefix))
            {
                done = true;
                return false;
            }

            if (bufsrc == -1)
            {
                vi[height - 1]++;
            }
            else
            {
                bi[bufsrc]++;
            }

            byte[]? found = ok ? m.Value : null;
            for (int i = height - 2; i >= 0; i--)
            {
                for (Blk? b = blocks[i]; b is not null && bi[i] < b.MessageCount; bi[i]++)
                {
                    Message u = b.GetMessage(bi[i]);
                    if (Keys.Compare(m.Key, u.Key) != 0)
                    {
                        break;
                    }

                    found = Tree.Apply(found, u);
                }
            }

            Key = m.Key;
            if (found is not null)
            {
                Value = found;
                return true;
            }
        }

        return false;
    }

    // btexit
    public void Exit()
    {
        Array.Clear(blocks);
        height = 0;
    }

    // A key first met in a buffer starts with an insert, or with a clear or clobber that leaves nothing.
    private static bool Starts(Message m) => m.Op switch
    {
        MessageOp.Insert => true,
        MessageOp.ClearBlock or MessageOp.Clobber => false,
        _ => throw Tree.Broken(),
    };
}
