namespace NinePSharp.Fog.Gefs;

// The block store on a device: blocks allocated from its arenas, written in place and read back
// checked against their pointers' hashes.
internal sealed class Allocator : BlockStore
{
    private const long B = Format.BlockSize;

    private readonly Device device;
    private readonly List<Bptr> pending = [];
    private readonly List<Bptr> killed = [];
    private long roundRobin;

    private Allocator(Device device, Arena[] arenas)
    {
        this.device = device;
        Arenas = arenas;
    }

    public IReadOnlyList<Arena> Arenas { get; }

    public bool UseReserve { get; set; }

    // Blocks freed since the last commit that its root may still use, and so are reused only once the
    // next commit is durable.
    public IReadOnlyList<Bptr> Killed => killed;

    // ream's arenas: the first and last blocks kept for superblocks, the rest divided evenly. A
    // device is always a whole number of blocks.
    public static Allocator Ream(Device device, int arenas)
    {
        long size = device.Size - (2 * B);
        long span = size / arenas;
        span -= span % B;
        if (span < 10 * B)
        {
            throw new GefsException("device too small");
        }

        return new Allocator(device, [.. Enumerable.Range(0, arenas).Select(i => Arena.Ream(device, B + (i * span), span - (2 * B)))]);
    }

    public static Allocator Open(Device device, IReadOnlyList<Bptr> arenas, long syncGen)
        => new(device, [.. arenas.Select(p => Arena.Load(device, p, syncGen))]);

    public override Blk Get(Bptr bp)
    {
        var bytes = new byte[B];
        device.Read(bp.Addr, bytes);
        return Blk.Read(bytes, bp);
    }

    // freeblk and freebp: a block born in the generation its tree is writing goes back to its arena
    // once nothing can still be reading it. One born earlier is in a committed root: a tree without
    // snapshots retires it at the next commit, and a snapshot tree puts it on a deadlist, unless it
    // was born at or before the snapshot the tree forked from, whose own chain still holds it.
    public override void Free(Tree t, Bptr bp)
    {
        if (bp.Gen >= t.Gen)
        {
            pending.Add(bp);
        }
        else if (t.Deadlists is not { } deadlists)
        {
            killed.Add(bp);
        }
        else if (bp.Gen > t.Base)
        {
            deadlists.Kill(t, bp);
        }
    }

    // A block no committed root will use once the next commit is durable.
    public void Retire(long address) => killed.Add(new Bptr(address, default, default));

    public void Reclaim()
    {
        foreach (Bptr bp in pending)
        {
            ArenaOf(bp.Addr).Deallocate(bp.Addr);
        }

        pending.Clear();
    }

    // sync's part for the arenas. The frees of blocks the previous root used, and of logs replaced by
    // compression, are logged; each log ends with a barrier for the generation and is written, then
    // the first headers; commit then writes the superblock naming them, and after it the second
    // headers. A crash before commit leaves the old superblock, whose hashes no longer match the
    // first headers but still match the second; a crash after leaves the new one, matching the first.
    // Only then are the freed blocks reused.
    public void Sync(long gen, bool compress, Action<IReadOnlyList<Bptr>> commit)
    {
        var retired = new List<long>();
        foreach (Arena a in Arenas)
        {
            if (compress && a.LogBlocks >= 2 * a.CompressedBlocks && a.Compress() is { } old)
            {
                retired.AddRange(old);
            }
        }

        retired.AddRange(killed.Select(k => k.Addr));
        killed.Clear();
        foreach (long b in retired)
        {
            ArenaOf(b).Retire(b);
        }

        foreach (Arena a in Arenas)
        {
            a.Barrier(gen);
            a.FlushLog();
        }

        device.Flush();
        Bptr[] pointers = [.. Arenas.Select(a => a.WriteHeader(0))];
        device.Flush();
        commit(pointers);
        foreach (Arena a in Arenas)
        {
            a.WriteHeader(1);
        }

        device.Flush();
        foreach (long b in retired)
        {
            ArenaOf(b).Release(b);
        }
    }

    // pickarena and blkalloc: arenas taken in turn, moving on every 2048 allocations, and the next
    // tried when one is full.
    protected override long Allocate(BlockType type)
    {
        int first = (int)((++roundRobin / 2048) % Arenas.Count);
        foreach (Arena a in Arenas.Skip(first).Concat(Arenas.Take(first)))
        {
            if (a.Allocate(useReserve: UseReserve) is { } b)
            {
                return b;
            }
        }

        throw new GefsException("file system full");
    }

    protected override void Write(Blk b) => device.Write(b.Address, b.Buffer);

    // Arenas are the same size and follow the first superblock.
    private Arena ArenaOf(long address) => Arenas[(int)((address - B) / (Arenas[0].Size + (2 * B)))];
}
