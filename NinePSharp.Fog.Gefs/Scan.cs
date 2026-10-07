namespace NinePSharp.Fog.Gefs;

// btnewscan, btenter, btnext and btexit: the keys beginning with a prefix, in order, with buffered
// messages applied. A scan can be left and entered again after the tree changes, and carries on
// after the last key it gave.
internal sealed class Scan(ReadOnlySpan<byte> prefix)
{
    private readonly byte[] prefix = prefix.ToArray();
    private Level[] path = [];
    private BlockStore? store;
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
        var (bp, height) = t.GetRoot();
        path = new Level[height];
        Blk b = store.Get(bp);
        for (int i = 0; i < path.Length; i++)
        {
            int vi = b.BlockSearch(Key, out bool same);
            if (b.Type == BlockType.Leaf)
            {
                path[i] = new Level(b) { Vi = vi == -1 || !same || !first ? vi + 1 : vi };
            }
            else
            {
                int bi = b.BufferSearch(Key, out same);
                if (!same || !first)
                {
                    // Past the messages for the key already given, or for the key before the place.
                    bi = Math.Max(bi, 0);
                    while (bi < b.MessageCount && Keys.Compare(Key, b.GetMessage(bi).Key) >= 0)
                    {
                        bi++;
                    }
                }

                vi = Math.Max(vi, 0);
                path[i] = new Level(b) { Vi = vi, Bi = bi };
                b = store.Get(Blk.GetPointer(b.GetValue(vi).Value).Pointer);
            }
        }

        first = false;
    }

    // btnext: the least key among the leaf's next value and each pivot's next buffered message.
    public bool Next()
    {
        while (path.Length > 0 && !done)
        {
            // Drop the levels with nothing left; the level above moves to its next child.
            int start = path.Length;
            for (int i = path.Length - 1; path[i].B is not { } b || (path[i].Vi >= b.ValueCount && path[i].Bi >= b.MessageCount); i--)
            {
                if (i == 0)
                {
                    done = true;
                    return false;
                }

                path[i] = new Level(null);
                path[i - 1].Vi++;
                start = i;
            }

            Level above = path[start - 1];
            bool leaf = above.Vi < above.B!.ValueCount;
            Message m;
            if (leaf)
            {
                for (int i = start; i < path.Length; i++)
                {
                    path[i].B = store!.Get(Blk.GetPointer(path[i - 1].B!.GetValue(path[i - 1].Vi).Value).Pointer);
                }

                var (key, value) = path[^1].B!.GetValue(path[^1].Vi);
                m = new Message(MessageOp.Insert, key, value);
            }
            else
            {
                m = above.B.GetMessage(above.Bi);
            }

            for (int i = path.Length - 2; i >= 0; i--)
            {
                if (path[i].B is { } b && path[i].Bi < b.MessageCount && b.GetMessage(path[i].Bi) is var n && Keys.Compare(n.Key, m.Key) < 0)
                {
                    m = n;
                    leaf = false;
                }
            }

            if (!m.Key.AsSpan().StartsWith(prefix))
            {
                done = true;
                return false;
            }

            byte[]? found = null;
            if (leaf)
            {
                found = m.Value;
                path[^1].Vi++;
            }
            else if (m.Op is not (MessageOp.Insert or MessageOp.ClearBlock or MessageOp.Clobber))
            {
                throw Tree.Broken();
            }

            // Every message buffered for the key, from the deepest buffer up.
            for (int i = path.Length - 2; i >= 0; i--)
            {
                for (Level l = path[i]; l.B is { } b && l.Bi < b.MessageCount && Keys.Compare(m.Key, b.GetMessage(l.Bi).Key) == 0; l.Bi++)
                {
                    found = Tree.Apply(found, b.GetMessage(l.Bi));
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
    public void Exit() => path = [];

    private sealed class Level(Blk? b)
    {
        public Blk? B { get; set; } = b;

        public int Vi { get; set; }

        public int Bi { get; set; }
    }
}
