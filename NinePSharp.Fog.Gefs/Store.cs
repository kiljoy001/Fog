using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// gefs's Gefs: a device holding arenas and a tree of snapshots, all committed at once by its
// superblocks, the first at the device's first block and a backup at its last.
//
// The snapshot tree names each snapshot by label and holds its root, its predecessor and successor,
// the snapshot it forked from, and counts of the labels and forks that keep it. A label may be
// mutable: its tree moves on to a new snapshot at each commit that changed it. A block is held from
// the snapshot it was born in until the one that freed it, which lists it on a deadlist by birth.
// Deleting a snapshot frees what only it held. Unlike gefs, a deadlist merged into another is copied
// rather than spliced in place, and every free is logged before the barrier of the commit that makes
// it safe.
internal sealed class Store : IDeadlists
{
    private const long B = Format.BlockSize;
    private const int Mutable = 1;
    private const int DeadlistSpace = Format.BlockSize - Blk.LogHeaderSize;

    private static readonly Bptr None = new(-1, default, default);
    private static readonly string[] Reserved = ["empty", "dump"];

    private readonly Device device;
    private readonly Dictionary<string, Mounted> mounts = [];
    private readonly Dictionary<(long Gen, long Birth), Deadlist> deadlists = [];
    private long nextGen;

    private Store(Device device, Allocator allocator, Tree snaps, long generation, long nextQid, long nextGen)
    {
        this.device = device;
        this.nextGen = nextGen;
        Allocator = allocator;
        Snaps = snaps;
        Generation = generation;
        NextQid = nextQid;
    }

    public Allocator Allocator { get; }

    public Tree Snaps { get; }

    // The generation last committed.
    public long Generation { get; private set; }

    public long NextQid { get; private set; }

    // Deadlists with blocks not yet written; a commit writes them all.
    public int OpenDeadlists => deadlists.Count;

    // reamfs: arenas, and a snapshot tree holding an empty snapshot, and main forked from it, both a
    // root directory owned by the reaming user.
    public static Store Ream(Device device, int arenas, string owner = "adm", TimeProvider? clock = null)
    {
        Allocator allocator = Allocator.Ream(device, arenas);
        Blk root = allocator.New(BlockType.Leaf, 0);
        long now = Nanoseconds(clock ?? TimeProvider.System);
        var dir = new Dir(string.Empty, new Qid(0, 0, 0x80), 0x80000000u | 0b111_111_101, now, now, 0, owner, owner, owner);
        root.SetValue(dir.Key(-1), dir.Value());
        root.SetValue(Keys.Up(0), dir.Key(-1));
        allocator.Enqueue(root);
        var store = new Store(device, allocator, Tree.Create(allocator), 0, 1, 2);
        store.Snaps.Upsert(
            Label("empty", 0, 0),
            Entry(new TreeEntry(1, 1, 1, 0, 0, -1, -1, -1, root.Pointer)),
            Label("main", 1, Mutable),
            Entry(new TreeEntry(0, 1, 1, 0, 1, -1, -1, 0, root.Pointer)));
        store.Commit();
        return store;
    }

    // loadfs: the first superblock, or the backup when the first is unusable, then the arenas
    // replayed to the generation it committed.
    public static Store Open(Device device)
    {
        Superblock sb;
        try
        {
            sb = ReadSuperblock(device, 0);
        }
        catch (GefsException)
        {
            sb = ReadSuperblock(device, device.Size - B);
        }

        if (sb.BlockSize != Format.BlockSize || sb.BufferSpace != Format.BufferSpace)
        {
            throw new GefsException("incompatible block size");
        }

        Allocator allocator = Allocator.Open(device, sb.Arenas, sb.SyncGen);
        return new Store(device, allocator, new Tree(allocator, sb.Snap.Root, sb.Snap.Height), sb.SyncGen, sb.NextQid, sb.NextGen);
    }

    public static long Nanoseconds(TimeProvider clock) => (clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks * 100;

    public long NewQid() => NextQid++;

    // opensnap: the tree a label names, writing in a generation of its own.
    public Tree Mount(string label)
    {
        if (mounts.TryGetValue(label, out Mounted? m))
        {
            return m.Tree;
        }

        var (gen, flags) = FindLabel(label) ?? throw new GefsException("snap -- does not exist");
        TreeEntry e = Snapshot(gen);
        bool mutable = (flags & Mutable) != 0;
        var t = new Tree(Allocator, e.Root, e.Height) { Gen = nextGen++, Base = e.Base, Deadlists = this, ReadOnly = !mutable };
        mounts[label] = new Mounted(label, mutable, gen, t);
        return t;
    }

    public void Unmount(string label)
    {
        if (mounts.TryGetValue(label, out Mounted? m) && m.Tree.Dirty)
        {
            throw new GefsException("snap -- has uncommitted changes");
        }

        mounts.Remove(label);
    }

    // tagsnap: a new name for the snapshot a label names, after committing a mounted tree's changes
    // to it. A mutable name forks a tree of its own from it.
    public void Tag(string label, string name, bool mutable)
    {
        Unreserved(name);
        if (FindLabel(name) is not null)
        {
            throw new GefsException("snap -- already exists");
        }

        long gen;
        if (mounts.TryGetValue(label, out Mounted? m))
        {
            if (m.Mutable && m.Tree.Dirty)
            {
                Update(m);
            }

            gen = m.Gen;
        }
        else
        {
            gen = (FindLabel(label) ?? throw new GefsException("snap -- does not exist")).Gen;
        }

        TreeEntry s = Snapshot(gen);
        if (mutable)
        {
            long fork = nextGen++;
            Snaps.Upsert(
                Retag(MessageOp.Incref, s.Gen, 0, 0, 1),
                Label(name, fork, Mutable),
                Entry(new TreeEntry(0, 1, s.Height, s.Flag, fork, -1, -1, s.Gen, s.Root)));
        }
        else
        {
            Snaps.Upsert(Retag(MessageOp.Relink, s.Gen, s.Succ, 1, 0), Label(name, s.Gen, 0));
        }
    }

    // snapfs's delete: the name goes, and with it the snapshot once nothing keeps it. A snapshot with
    // no successor has what only it held swept, and an older one kept only by it follows.
    public void Delete(string name)
    {
        Unreserved(name);
        if (mounts.ContainsKey(name))
        {
            throw new GefsException("snap -- is currently mounted");
        }

        var (gen, _) = FindLabel(name) ?? throw new GefsException("snap -- does not exist");
        TreeEntry s = Snapshot(gen);
        for (bool sweep = Delete(s, s.Succ, name); sweep; sweep = Delete(s, s.Succ, null))
        {
            long older = Older(s);
            Sweep(s, older);
            if (older == -1 || Snapshot(older) is not { Labels: 0, Refs: 0 } next)
            {
                break;
            }

            s = next;
        }
    }

    public void Kill(Tree t, Bptr bp) => Append((t.Gen, bp.Gen), bp.Addr);

    public List<long> DeadlistContents(long gen, long birth) => Contents(Load((gen, birth)));

    public int DeadlistBlocks(long gen, long birth) => Chain(Load((gen, birth))).Count();

    // sync: each mutable tree that changed becomes a new snapshot, the deadlists are written, and the
    // arenas commit it by having both superblocks written between their first and second headers.
    public void Commit()
    {
        foreach (Mounted m in mounts.Values.Where(m => m.Tree.Dirty).ToList())
        {
            Update(m);
        }

        FlushDeadlists();
        long gen = Generation + 1;
        Allocator.Sync(gen, true, arenas =>
        {
            var sb = new Superblock(Format.BlockSize, Format.BufferSpace, new TreeRoot(Snaps.Height, Snaps.Root), default, default, 0, NextQid, nextGen, gen, [.. arenas]);
            var block = new byte[B];
            sb.Write(block);
            device.Write(0, block);
            device.Flush();
            device.Write(device.Size - B, block);
            device.Flush();
        });
        Generation = gen;
    }

    public (long Gen, int Flags)? FindLabel(string label)
        => Snaps.Lookup(Keys.Label(label)) is { } v ? (BinaryPrimitives.ReadInt64BigEndian(v.AsSpan(1)), BinaryPrimitives.ReadInt32BigEndian(v.AsSpan(9))) : null;

    public TreeEntry Snapshot(long gen) => TreeEntry.Read(Snaps.Lookup(Keys.Snap(gen)) ?? throw new GefsException("internal error"));

    private static Message Label(string name, long gen, int flags)
    {
        var value = new byte[1 + 8 + 4];
        Keys.Snap(gen).CopyTo(value, 0);
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(9), flags);
        return new Message(MessageOp.Insert, Keys.Label(name), value);
    }

    private static Message Entry(TreeEntry e) => new(MessageOp.Insert, Keys.Snap(e.Gen), e.Value());

    // retag2kv: a change to a snapshot's successor or predecessor, and to its label and fork counts.
    private static Message Retag(MessageOp op, long gen, long link, int labels, int refs)
    {
        var value = new byte[8 + 1 + 1];
        BinaryPrimitives.WriteInt64BigEndian(value, link);
        value[8] = (byte)(sbyte)labels;
        value[9] = (byte)(sbyte)refs;
        return new Message(op, Keys.Snap(gen), value);
    }

    private static Superblock ReadSuperblock(Device device, long offset)
    {
        var block = new byte[B];
        device.Read(offset, block);
        return Superblock.Read(block);
    }

    private static void Unreserved(string name)
    {
        if (Reserved.Contains(name))
        {
            throw new GefsException("snap -- reserved name");
        }
    }

    // The newest snapshot older than s that it shares blocks with.
    private static long Older(TreeEntry s) => s.Pred != -1 ? s.Pred : s.Base;

    // updatesnap: the tree's state becomes the snapshot its generation names. The old snapshot keeps
    // its place in the chain if another label or a fork keeps it, and is deleted otherwise.
    private void Update(Mounted m)
    {
        TreeEntry o = Snapshot(m.Gen);
        Tree t = m.Tree;
        long gen = t.Gen;
        bool gone = o.Labels == 1 && o.Refs == 0;
        long pred = gone ? o.Pred : o.Gen;
        var msgs = new List<Message>();
        if (!gone)
        {
            msgs.Add(Retag(MessageOp.Relink, o.Gen, gen, -1, 0));
        }

        msgs.Add(Entry(new TreeEntry(0, 1, t.Height, o.Flag, gen, pred, -1, o.Base, t.Root)));
        msgs.Add(Label(m.Label, gen, Mutable));
        Snaps.Upsert([.. msgs]);
        if (gone)
        {
            Delete(o with { Labels = 0 }, gen, null);
        }

        m.Gen = gen;
        t.Gen = nextGen++;
        t.Dirty = false;
    }

    // delsnap: one label fewer, and once nothing keeps the snapshot, its links mended around it, its
    // entry deleted and its blocks reclaimed. Says whether what only it held must be swept.
    private bool Delete(TreeEntry s, long succ, string? name)
    {
        FlushDeadlists();
        var msgs = new List<Message>();
        int labels = s.Labels;
        if (name is not null)
        {
            msgs.Add(new Message(MessageOp.Delete, Keys.Label(name), []));
            labels--;
        }

        bool del = labels == 0 && s.Refs == 0;
        if (del)
        {
            if (s.Pred != -1)
            {
                msgs.Add(Retag(MessageOp.Relink, s.Pred, succ, 0, 0));
            }

            if (s.Succ != -1)
            {
                msgs.Add(Retag(MessageOp.Reprev, s.Succ, s.Pred, 0, 0));
            }

            if (s.Pred == -1 && succ == -1)
            {
                msgs.Add(Retag(MessageOp.Incref, s.Base, 0, 0, -1));
            }

            msgs.Add(new Message(MessageOp.Delete, Keys.Snap(s.Gen), []));
        }
        else
        {
            msgs.Add(Retag(MessageOp.Relink, s.Gen, s.Succ, -1, 0));
        }

        Snaps.Upsert([.. msgs]);
        if (del)
        {
            Reclaim(s.Gen, succ, Older(s));
        }

        return del && succ == -1;
    }

    // reclaimblocks: blocks the successor freed that were born after the snapshot before the deleted
    // one were held by the deleted one alone, and are freed. The deleted snapshot's own deadlists hold
    // only blocks born no later than that older snapshot, which still holds them: their successor
    // now frees them, or with no successor, the older snapshot's own tree holds them. They were
    // emptied of later births when it became a snapshot, as this does for the successor now.
    private void Reclaim(long gen, long succ, long older)
    {
        foreach (Deadlist dl in DeadlistsOf(gen))
        {
            if (succ != -1)
            {
                foreach (long addr in Contents(dl))
                {
                    Append((succ, dl.Birth), addr);
                }
            }

            Discard(dl, false);
        }

        foreach (Deadlist dl in DeadlistsOf(succ).Where(dl => dl.Birth > older))
        {
            Discard(dl, true);
        }
    }

    // sweeptree and freetree: the file data and tree blocks of a snapshot with no successor that
    // were born after the snapshot before it.
    private void Sweep(TreeEntry s, long older)
    {
        var t = new Tree(Allocator, s.Root, s.Height);
        var scan = new Scan([(byte)KeyType.Data]);
        scan.Enter(t);
        while (scan.Next())
        {
            Bptr bp = Bptr.Read(scan.Value);
            if (bp.Gen > older)
            {
                Allocator.Retire(bp.Addr);
            }
        }

        FreeTree(s.Root, s.Height, older);
    }

    private void FreeTree(Bptr bp, int height, long older)
    {
        Blk b = Allocator.Get(bp);
        for (int i = 0; height > 1 && i < b.ValueCount; i++)
        {
            FreeTree(Blk.GetPointer(b.GetValue(i).Value).Pointer, height - 1, older);
        }

        if (bp.Gen > older)
        {
            Allocator.Retire(bp.Addr);
        }
    }

    private List<Deadlist> DeadlistsOf(long gen)
    {
        var found = new List<Deadlist>();
        var scan = new Scan(Keys.Deadlist(gen, 0)[..9]);
        scan.Enter(Snaps);
        while (scan.Next())
        {
            found.Add(new Deadlist(gen, BinaryPrimitives.ReadInt64BigEndian(scan.Key.AsSpan(9)), Bptr.Read(scan.Value)));
        }

        return found;
    }

    // A deadlist with a block still open, or as the snapshot tree last recorded it.
    private Deadlist Load((long Gen, long Birth) key)
        => deadlists.TryGetValue(key, out Deadlist? dl)
            ? dl
            : new Deadlist(key.Gen, key.Birth, Snaps.Lookup(Keys.Deadlist(key.Gen, key.Birth)) is { } v ? Bptr.Read(v) : None);

    // killblk: an address appended to the deadlist's open block, a new one begun when it is full.
    private void Append((long Gen, long Birth) key, long addr)
    {
        Deadlist dl = Load(key);
        if (dl.Ins is not { } ins || ins.LogSize + 8 > DeadlistSpace)
        {
            if (dl.Ins is { } full)
            {
                Allocator.Enqueue(full);
                dl.Head = full.Pointer;
            }

            ins = Allocator.New(BlockType.Deadlist, Snaps.Gen);
            ins.LogNext = dl.Head;
            dl.Ins = ins;
            deadlists[key] = dl;
        }

        BinaryPrimitives.WriteInt64BigEndian(ins.Data[ins.LogSize..], addr);
        ins.LogSize += 8;
    }

    // A written deadlist's blocks; only Append and FlushDeadlists see one with an open block.
    private IEnumerable<Blk> Chain(Deadlist dl)
    {
        for (Bptr at = dl.Head; at.Addr != -1;)
        {
            Blk b = Allocator.Get(at);
            yield return b;
            at = b.LogNext;
        }
    }

    private List<long> Contents(Deadlist dl)
        => [.. Chain(dl).SelectMany(b => Enumerable.Range(0, b.LogSize / 8).Select(i => BinaryPrimitives.ReadInt64BigEndian(b.Data[(i * 8)..])))];

    // A deadlist's blocks retired, and the blocks it lists too when nothing else holds them.
    private void Discard(Deadlist dl, bool contents)
    {
        if (contents)
        {
            foreach (long addr in Contents(dl))
            {
                Allocator.Retire(addr);
            }
        }

        foreach (Blk b in Chain(dl).ToList())
        {
            Allocator.Retire(b.Address);
        }

        Snaps.Upsert(new Message(MessageOp.Delete, Keys.Deadlist(dl.Gen, dl.Birth), []));
    }

    // dlsync: each deadlist's open block written and its head recorded in the snapshot tree.
    private void FlushDeadlists()
    {
        var msgs = new List<Message>();
        foreach (Deadlist dl in deadlists.Values)
        {
            if (dl.Ins is { } ins)
            {
                Allocator.Enqueue(ins);
                dl.Head = ins.Pointer;
                dl.Ins = null;
                var value = new byte[Format.PointerSize];
                dl.Head.Write(value);
                msgs.Add(new Message(MessageOp.Insert, Keys.Deadlist(dl.Gen, dl.Birth), value));
            }
        }

        deadlists.Clear();
        foreach (Message[] chunk in msgs.Chunk(64))
        {
            Snaps.Upsert(chunk);
        }
    }

    private sealed class Mounted(string label, bool mutable, long gen, Tree tree)
    {
        public string Label => label;

        public bool Mutable => mutable;

        public long Gen { get; set; } = gen;

        public Tree Tree => tree;
    }
}
