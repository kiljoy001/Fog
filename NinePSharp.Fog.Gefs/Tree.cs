namespace NinePSharp.Fog.Gefs;

// tree.c's Bε tree. Pivots buffer messages for their children, and an upsert rewrites the path it
// flushes along from the bottom up; the blocks it replaces go back to the store.
internal sealed class Tree(BlockStore store, Bptr root, int height)
{
    private static readonly Comparer<byte[]> KeyOrder = Comparer<byte[]>.Create((a, b) => Keys.Compare(a, b));

    private readonly object gate = new();
    private Bptr root = root;
    private int height = height;

    private enum PathOp
    {
        Mod,
        Rotate,
        Split,
        Merge,
    }

    public Bptr Root => GetRoot().Root;

    public int Height => GetRoot().Height;

    internal BlockStore Store => store;

    public static Tree Create(BlockStore store)
    {
        Blk b = store.New(BlockType.Leaf);
        store.Enqueue(b);
        return new Tree(store, b.Pointer, 1);
    }

    // btupsert
    public void Upsert(params Message[] messages)
    {
        Message[] msg = [.. messages.OrderBy(m => m.Key, KeyOrder)];
        int sz = 0;
        foreach (Message m in msg)
        {
            if (m.Key.Length > Format.MaxEntry)
            {
                throw new GefsException("key too large");
            }

            if (m.Value.Length > Format.MaxInline)
            {
                throw new GefsException("value too large");
            }

            sz += MessageSize(m);
        }

        // A batch that would not fit an empty buffer could split a leaf root, merge it back and go
        // round forever.
        if ((2 * msg.Length) + sz > Blk.BufferSpace)
        {
            throw new GefsException("upsert too large");
        }

        int npull = 0;
        bool degen;
        do
        {
            var (bp, h) = GetRoot();
            if (h + 1 >= Format.MaxHeight)
            {
                throw new GefsException("tree exceeds max height");
            }

            Blk b = store.Get(bp);

            // A root is left with one child only partway through an upsert, after it has pulled messages.
            if (npull == 0 && b.Type == BlockType.Pivot && !b.BufferFull(msg.Length, sz))
            {
                FastUpsert(b, msg);
                return;
            }

            // A split can add a level, so the path has room for a new root above the old one.
            var path = new Path[h + 2];
            path[0] = new Path { Ins = msg, Lo = npull, Hi = msg.Length, Size = sz };
            int npath = 1;
            while (b.Type == BlockType.Pivot && (b.ValueCount <= 1 || b.BufferFull(msg.Length, path[npath - 1].Size)))
            {
                path[npath] = Victim(b);
                b = store.Get(Blk.GetPointer(b.GetValue(path[npath].Idx).Value).Pointer);
                npath++;
            }

            path[npath++] = new Path { B = b };
            Path rp = Flush(path, npath);
            if (rp.Left is { } rb)
            {
                int dh = path[0].Left is not null ? 1 : path[1].Left is not null ? 0 : -1;
                SetRoot(rb.Pointer, h + dh);

                // A merged root with messages still in its buffer has one child; go round again to
                // pull them down.
                degen = rb.Type == BlockType.Pivot && rb.ValueCount == 1;
            }
            else
            {
                // gefs never empties a tree; here a tree whose every entry is gone is an empty leaf again.
                Blk empty = store.New(BlockType.Leaf);
                store.Enqueue(empty);
                SetRoot(empty.Pointer, 1);
                degen = false;
            }

            npull += rp.Npull;
            FreePath(path, npath);
        }
        while (npull != msg.Length || degen);
    }

    // btlookup: the key's value in its leaf, with the messages buffered above it applied.
    public byte[]? Lookup(ReadOnlySpan<byte> key)
    {
        var (bp, h) = GetRoot();
        var p = new Blk?[h];
        p[0] = store.Get(bp);
        for (int i = 1; i < h; i++)
        {
            int at = p[i - 1]!.BlockSearch(key, out _);
            if (at == -1)
            {
                break;
            }

            p[i] = store.Get(Blk.GetPointer(p[i - 1]!.GetValue(at).Value).Pointer);
        }

        byte[]? value = null;
        if (p[h - 1] is { } leaf)
        {
            int at = leaf.BlockSearch(key, out bool found);
            value = found ? leaf.GetValue(at).Value : null;
        }

        for (int i = h - 2; i >= 0; i--)
        {
            if (p[i] is not { } b)
            {
                continue;
            }

            int j = b.BufferSearch(key, out bool same);
            if (!same)
            {
                continue;
            }

            Message m = b.GetMessage(j);
            if (value is not null || m.Op == MessageOp.Insert)
            {
                value = Messages.Apply(value!, m);
            }
            else if (m.Op is not (MessageOp.ClearBlock or MessageOp.Clobber))
            {
                throw new GefsException("internal error: missing insert");
            }

            for (j++; j < b.MessageCount; j++)
            {
                m = b.GetMessage(j);
                if (Keys.Compare(key, m.Key) != 0)
                {
                    break;
                }

                value = Apply(value, m);
            }
        }

        return value;
    }

    internal static GefsException Broken() => new("internal error: broken entry");

    // apply, refusing a change to a key that holds no value.
    internal static byte[]? Apply(byte[]? value, Message m)
        => value is null && m.Op is not (MessageOp.Insert or MessageOp.Delete or MessageOp.ClearBlock or MessageOp.Clobber)
            ? throw Broken()
            : Messages.Apply(value!, m);

    internal (Bptr Root, int Height) GetRoot()
    {
        lock (gate)
        {
            return (root, height);
        }
    }

    private static int MessageSize(Message m) => 2 + 1 + 2 + m.Key.Length + 2 + m.Value.Length;

    private static int ValueSize(byte[] key, byte[] value) => 2 + 2 + key.Length + 2 + value.Length;

    private static Message At(Path p, int i) => p.Ins is { } ins ? ins[i] : p.B!.GetMessage(i);

    // pullmsg: the i'th message of p's range, unless the range is spent or the message would not fit
    // in spc, after which nothing more is pulled.
    private static bool Pull(Path p, int i, out Message m, ref bool full, int spc)
    {
        m = default;
        if (i >= p.Hi || full)
        {
            return false;
        }

        m = At(p, i);
        full = MessageSize(m) > spc;
        return !full;
    }

    // victim: the child whose messages take the most of the buffer.
    private static Path Victim(Blk b)
    {
        var p = new Path { B = b };
        int j = 0;
        int maxsz = 0;

        // Values below the second pivot key go to the first child, and those from the last key on
        // to the last.
        for (int i = 1; i <= b.ValueCount; i++)
        {
            byte[]? next = i < b.ValueCount ? b.GetValue(i).Key : null;
            int cursz = 0;
            int lo = j;
            for (; j < b.MessageCount; j++)
            {
                Message m = b.GetMessage(j);
                if (next is not null && Keys.Compare(m.Key, next) >= 0)
                {
                    break;
                }

                cursz += MessageSize(m);
            }

            if (cursz > maxsz)
            {
                maxsz = cursz;
                (p.Lo, p.Hi, p.Size, p.Idx, p.Midx) = (lo, j, maxsz, i - 1, i - 1);
            }
        }

        return p;
    }

    // copyup: pointers to the blocks a child became, each keyed by the least key it holds.
    private static int CopyUp(Blk n, Path pp)
    {
        int size = 0;
        foreach (Blk? b in (Blk?[])[pp.Left, pp.Right])
        {
            if (b is null)
            {
                continue;
            }

            var (key, value) = b.GetValue(0);
            if (b.MessageCount > 0)
            {
                key = new[] { key, b.GetMessage(0).Key }.Min(KeyOrder)!;
            }

            n.SetPointer(key, b.Pointer, b.Fill);
            size += ValueSize(key, value);
        }

        return size;
    }

    // splitidx: the first buffered message of l and r at or after key.
    private static int SplitIndex(Blk l, Blk r, byte[] key, int idx)
    {
        int i = idx;
        for (; i < l.MessageCount + r.MessageCount; i++)
        {
            Message n = i < l.MessageCount ? l.GetMessage(i) : r.GetMessage(i - l.MessageCount);
            if (Keys.Compare(key, n.Key) <= 0)
            {
                break;
            }
        }

        return i;
    }

    // A pivot's buffered messages, less those its child pulled down.
    private static List<Message> Kept(Blk b, Path p, Path? pp)
        => [.. Enumerable.Range(0, b.MessageCount).Where(k => k < p.Lo || k >= p.Lo + (pp?.Npull ?? 0)).Select(b.GetMessage)];

    private static bool IsData(byte[] key) => key.Length > 0 && key[0] == (byte)KeyType.Data;

    private void SetRoot(Bptr bp, int h)
    {
        lock (gate)
        {
            (root, height) = (bp, h);
        }
    }

    // fastupsert: a pivot root with room takes the messages into its buffer, each after those
    // already there for its key.
    private void FastUpsert(Blk b, Message[] msg)
    {
        Blk r = store.Duplicate(b);
        int nbuf = r.MessageCount;
        foreach (Message m in msg)
        {
            r.SetMessage(m);
        }

        for (int i = 0; i < msg.Length; i++)
        {
            int lo = 0;
            int hi = nbuf + i - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Keys.Compare(msg[i].Key, r.GetMessage(mid).Key) < 0)
                {
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            r.MoveMessage(nbuf + i, lo);
        }

        store.Enqueue(r);
        SetRoot(r.Pointer, Height);
        store.Free(b.Pointer);
    }

    // setb
    private Blk? Replace(Blk? old, Blk b)
    {
        if (old is not null)
        {
            store.Free(old.Pointer);
        }

        if (b.ValueCount == 0)
        {
            store.Free(b.Pointer);
            return null;
        }

        store.Enqueue(b);
        return b;
    }

    // A file's data block goes back to the store when the entry pointing at it is replaced or cleared.
    private void FreeData(byte[] key, byte[] value, Message m)
    {
        if (IsData(key) && m.Op is MessageOp.ClearBlock or MessageOp.Insert or MessageOp.Delete)
        {
            store.Free(Bptr.Read(value));
        }
    }

    // updateleaf's and splitleaf's copy loop: the messages that follow for the same key apply to it.
    private byte[]? Absorb(Path up, Path p, ref int j, ref bool full, int spc, byte[] key, byte[]? value)
    {
        while (Pull(up, j, out Message m, ref full, spc) && Keys.Compare(key, m.Key) == 0)
        {
            if (value is not null)
            {
                FreeData(key, value, m);
            }

            p.PullSize += MessageSize(m);
            value = Apply(value, m);
            j++;
        }

        return value;
    }

    // A value meeting a message for its key; gefs leaves this first message out of the pulled size.
    private byte[]? Merged(Path up, Path p, ref int j, ref bool full, int spc, byte[] key, byte[] value)
    {
        Message m = At(up, j++);
        FreeData(key, value, m);
        return Absorb(up, p, ref j, ref full, spc, key, Apply(value, m));
    }

    // A message for a key the leaf lacks: an insert, or a clear or clobber that leaves nothing.
    private byte[]? Started(Path up, Path p, ref int j, ref bool full, ref int spc)
    {
        Message m = At(up, j++);
        byte[]? value = null;
        if (m.Op is not (MessageOp.ClearBlock or MessageOp.Clobber))
        {
            if (m.Op != MessageOp.Insert)
            {
                throw Broken();
            }

            spc -= ValueSize(m.Key, m.Value);
            p.PullSize += MessageSize(m);
            value = m.Value;
        }

        return Absorb(up, p, ref j, ref full, spc, m.Key, value);
    }

    // updateleaf: the leaf repacked with what its parent pulls down. Flush only updates a leaf with
    // room for every message in the range, so unlike a split it never runs short.
    private void UpdateLeaf(Path up, Path p)
    {
        Blk b = p.B!;
        Blk n = store.New(b.Type);
        int i = 0;
        int j = up.Lo;
        int spc = int.MaxValue;
        bool full = false;
        while (i < b.ValueCount || j < up.Hi)
        {
            int c = i == b.ValueCount ? 1 : j == up.Hi ? -1 : Keys.Compare(b.GetValue(i).Key, At(up, j).Key);
            byte[] key;
            byte[]? value;
            if (c < 0)
            {
                (key, value) = b.GetValue(i++);
            }
            else if (c == 0)
            {
                (key, byte[] old) = b.GetValue(i++);
                value = Merged(up, p, ref j, ref full, spc, key, old);
            }
            else
            {
                key = At(up, j).Key;
                value = Started(up, p, ref j, ref full, ref spc);
            }

            if (value is not null)
            {
                n.SetValue(key, value);
            }
        }

        p.Npull = j - up.Lo;
        p.Left = Replace(p.Left, n);
    }

    // updatepiv: the pivot repacked, with pointers to what its child became, the messages its child
    // took dropped, and as many of its parent's messages as fit pulled in.
    private void UpdatePivot(Path up, Path p, Path? pp)
    {
        Blk b = p.B!;
        Blk n = store.New(b.Type);
        for (int i = 0; i < b.ValueCount; i++)
        {
            if (pp is not null && i == p.Midx)
            {
                CopyUp(n, pp);
                if (pp.Op is PathOp.Rotate or PathOp.Merge)
                {
                    i++;
                }
            }
            else
            {
                var (key, value) = b.GetValue(i);
                n.SetValue(key, value);
            }
        }

        List<Message> kept = Kept(b, p, pp);
        int k = 0;
        int j = up.Lo;
        int sz = 0;
        bool full = false;

        // The room to pull into is what the buffer has, plus what the child pulled out of it.
        int spc = Blk.BufferSpace - ((2 * b.MessageCount) + b.MessageSize) + (pp?.PullSize ?? 0);
        while (k < kept.Count)
        {
            Message m = kept[k];
            if (!Pull(up, j, out Message u, ref full, spc - sz) || Keys.Compare(m.Key, u.Key) <= 0)
            {
                n.SetMessage(m);
                k++;
                continue;
            }

            byte[] key = u.Key;
            while (Pull(up, j, out u, ref full, spc) && Keys.Compare(key, u.Key) == 0)
            {
                n.SetMessage(u);
                sz = MessageSize(u);
                p.PullSize += sz;
                spc -= sz;
                j++;
            }
        }

        while (Pull(up, j, out Message u, ref full, spc))
        {
            n.SetMessage(u);
            sz = MessageSize(u);
            p.PullSize += sz;
            spc -= sz;
            j++;
        }

        p.Npull = j - up.Lo;
        p.Left = Replace(p.Left, n);
    }

    // splitleaf: the leaf's values, with the messages that sort among them applied, shared between
    // two leaves of about half a block each, with at least two values in each.
    private void SplitLeaf(Path up, Path p)
    {
        Blk b = p.B!;
        Blk l = store.New(b.Type);
        Blk r = store.New(b.Type);
        Blk d = l;
        int i = 0;
        int j = up.Lo;
        int copied = 0;
        bool full = false;

        // gefs takes the lesser of this and half the leaf with its messages, but a leaf only splits when
        // those overflow a block, so the two differ at a single byte count.
        int halfsz = Blk.LeafSpace / 2;
        int spc = Blk.LeafSpace - (halfsz + Format.MaxMessage);
        while (i < b.ValueCount)
        {
            if (d == l && (i == b.ValueCount - 2 || (i >= 2 && copied >= halfsz)))
            {
                d = r;
                spc = Blk.LeafSpace - (halfsz + Format.MaxMessage);
            }

            var (key, value) = b.GetValue(i);
            int c = Pull(up, j, out Message m, ref full, spc) ? Keys.Compare(key, m.Key) : -1;
            byte[]? kept;
            if (c < 0)
            {
                i++;
                copied += ValueSize(key, value);
                kept = value;
            }
            else if (c == 0)
            {
                i++;
                copied += ValueSize(key, value);
                kept = Merged(up, p, ref j, ref full, spc, key, value);
            }
            else
            {
                copied += ValueSize(m.Key, m.Value);
                key = m.Key;
                kept = Started(up, p, ref j, ref full, ref spc);
            }

            if (kept is not null)
            {
                d.SetValue(key, kept);
            }
        }

        p.Npull = j - up.Lo;
        p.Op = PathOp.Split;
        p.Left = Replace(p.Left, l);
        p.Right = Replace(p.Right, r);
    }

    // splitpiv: the pointers shared between two pivots as splitleaf shares values, and the buffered
    // messages after them by key.
    private void SplitPivot(Path p, Path? pp)
    {
        Blk b = p.B!;
        Blk l = store.New(b.Type);
        Blk r = store.New(b.Type);
        Blk d = l;
        int copied = 0;
        int halfsz = ((2 * b.ValueCount) + b.ValueSize) / 2;
        for (int i = 0; i < b.ValueCount; i++)
        {
            // Unlike a leaf, a pivot only splits when nearly full, so half its size is always reached
            // with more than two pointers on each side.
            if (d == l && copied >= halfsz)
            {
                d = r;
            }

            if (pp is not null && i == p.Idx)
            {
                copied += CopyUp(d, pp);
                continue;
            }

            var (key, value) = b.GetValue(i);
            d.SetValue(key, value);
            copied += ValueSize(key, value);
        }

        d = l;
        byte[] mid = r.GetValue(0).Key;
        foreach (Message m in Kept(b, p, pp))
        {
            if (d == l && Keys.Compare(m.Key, mid) >= 0)
            {
                d = r;
            }

            d.SetMessage(m);
        }

        p.Op = PathOp.Split;
        p.Left = Replace(p.Left, l);
        p.Right = Replace(p.Right, r);
    }

    private void Merge(Path p, Path pp, int idx, Blk a, Blk b)
    {
        Blk d = store.New(a.Type);
        foreach (Blk s in (Blk[])[a, b])
        {
            for (int i = 0; i < s.ValueCount; i++)
            {
                var (key, value) = s.GetValue(i);
                d.SetValue(key, value);
            }
        }

        foreach (Blk s in (Blk[])[a, b])
        {
            for (int i = 0; i < s.MessageCount; i++)
            {
                d.SetMessage(s.GetMessage(i));
            }
        }

        p.Midx = idx;
        pp.Op = PathOp.Merge;
        pp.Left = Replace(pp.Left, d);
    }

    // rotate: the entries of two siblings shared evenly between two new ones, a pivot's messages
    // going with the pointers they will flush to.
    private void Rotate(Path p, Path pp, int midx, Blk a, Blk b, int halfpiv)
    {
        Blk l = store.New(a.Type);
        Blk r = store.New(a.Type);
        Blk d = l;
        int sz = 0;
        int sp = 0;
        foreach (Blk s in (Blk[])[a, b])
        {
            for (int i = 0; i < s.ValueCount; i++)
            {
                var (key, value) = s.GetValue(i);
                if (d == l)
                {
                    sp = SplitIndex(a, b, key, sp);
                    if (sz >= halfpiv)
                    {
                        d = r;
                    }
                }

                d.SetValue(key, value);
                sz += ValueSize(key, value);
            }
        }

        d = l;
        int o = 0;
        foreach (Blk s in (Blk[])[a, b])
        {
            for (int i = 0; i < s.MessageCount; i++)
            {
                if (o == sp)
                {
                    d = r;
                    o = 0;
                }

                d.SetMessage(s.GetMessage(i));
                o++;
            }
        }

        p.Midx = midx;
        pp.Op = PathOp.Rotate;
        pp.Left = Replace(pp.Left, l);
        pp.Right = Replace(pp.Right, r);
    }

    private bool RotateOrMerge(Path p, Path pp, int idx, Blk a, Blk b)
    {
        // gefs also checks the two buffers fit one, but trybalance only gets here when both blocks do.
        int na = (2 * a.ValueCount) + a.ValueSize;
        int nb = (2 * b.ValueCount) + b.ValueSize;
        if (na + nb < Blk.PivotSpace - (4 * Format.MaxMessage))
        {
            Merge(p, pp, idx, a, b);
            return true;
        }

        if (Math.Abs(na - nb) > 4 * Format.MaxMessage)
        {
            Rotate(p, pp, idx, a, b, (na + nb) / 2);
            return true;
        }

        return false;
    }

    // trybalance: a rewritten child that would fit in a block with a sibling merges or rotates with it.
    private void TryBalance(Path p, Path? pp, int idx)
    {
        if (p.Idx == -1 || pp?.Left is not { } m || pp.Op != PathOp.Mod)
        {
            return;
        }

        int spc = m.Type == BlockType.Leaf ? Blk.LeafSpace : Blk.PivotSpace;
        if (idx - 1 >= 0)
        {
            var (bp, fill) = Blk.GetPointer(p.B!.GetValue(idx - 1).Value);
            if (fill + m.Fill < spc)
            {
                Blk l = store.Get(bp);
                if (RotateOrMerge(p, pp, idx - 1, l, m))
                {
                    p.S = l;
                }

                return;
            }
        }

        if (idx + 1 < p.B!.ValueCount)
        {
            var (bp, fill) = Blk.GetPointer(p.B.GetValue(idx + 1).Value);
            if (fill + m.Fill < spc)
            {
                Blk r = store.Get(bp);
                if (RotateOrMerge(p, pp, idx, m, r))
                {
                    p.S = r;
                }
            }
        }
    }

    // flush: rewrite the path from the bottom up; the result holds the new root.
    private Path Flush(Path[] path, int npath)
    {
        Path? rp = null;
        Path? pp = null;
        int at = npath - 1;
        Path p = path[at];
        if (p.B!.Type == BlockType.Leaf)
        {
            if (!p.B.LeafFull(path[at - 1].Size))
            {
                UpdateLeaf(path[at - 1], p);
                rp = p;
            }
            else
            {
                SplitLeaf(path[at - 1], p);
            }

            pp = p;
            at--;
        }

        for (; at > 0; at--)
        {
            p = path[at];
            Path up = path[at - 1];

            // A path adds at most one key to a pivot, but children rewritten larger can need more, so
            // pivots split early.
            if (!p.B!.PivotFull(2))
            {
                TryBalance(p, pp, p.Idx);

                // A root merged down to one child that took its whole buffer gives way to that child.
                if (at == 1 && pp?.Left is not null && pp.Right is null && pp.Npull == p.B.MessageCount
                    && (p.B.ValueCount == 1 || (pp.Op == PathOp.Merge && p.B.ValueCount == 2)))
                {
                    pp.Npull = p.Npull;
                    return pp;
                }

                UpdatePivot(up, p, pp);
                rp = p;
            }
            else
            {
                SplitPivot(p, pp);
            }

            pp = p;
        }

        if (pp!.Left is not null && pp.Right is not null)
        {
            rp = path[0];
            Blk n = store.New(BlockType.Pivot);
            rp.Npull = pp.Npull;
            CopyUp(n, pp);
            store.Enqueue(n);
            rp.Left = n;
        }

        return rp!;
    }

    // freepath: the blocks the upsert replaced, and the siblings it merged or rotated away.
    private void FreePath(Path[] path, int npath)
    {
        for (int i = 0; i < npath; i++)
        {
            if (path[i].B is { } b)
            {
                store.Free(b.Pointer);
            }

            if (path[i].S is { } s)
            {
                store.Free(s.Pointer);
            }
        }
    }

    private sealed class Path
    {
        // Flowing down: the messages pulled from, or the block their range lies in, and the child.
        public Message[]? Ins { get; init; }

        public Blk? B { get; init; }

        public Blk? S { get; set; }

        public int Idx { get; set; }

        public int Lo { get; set; }

        public int Hi { get; set; }

        public int Size { get; set; }

        // Flowing up: what became of the block.
        public PathOp Op { get; set; }

        public Blk? Left { get; set; }

        public Blk? Right { get; set; }

        public int Midx { get; set; }

        public int Npull { get; set; }

        public int PullSize { get; set; }
    }
}
