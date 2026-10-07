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

    // The generation blocks are born in: the one being written.
    public long Gen { get; set; }

    public bool UseReserve { get; set; }

    // Blocks of earlier generations freed since; committed roots may still use them, so they belong
    // to a deadlist rather than back in an arena.
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

    // freeblk and freebp: a block born in the generation being written goes back to its arena once
    // nothing can still be reading it; one born earlier may be in a committed tree.
    public override void Free(Bptr bp) => (bp.Gen < Gen ? killed : pending).Add(bp);

    public void Reclaim()
    {
        // Arenas are the same size and follow the first superblock.
        foreach (Bptr bp in pending)
        {
            Arenas[(int)((bp.Addr - B) / (Arenas[0].Size + (2 * B)))].Deallocate(bp.Addr);
        }

        pending.Clear();
    }

    // sync's part for the arenas. Each log ends with a barrier for the generation and is written,
    // then the first headers; commit then writes the superblock naming them, and after it the second
    // headers. A crash before commit leaves the old superblock, whose hashes no longer match the
    // first headers but still match the second; a crash after leaves the new one, matching the first.
    public void Sync(long gen, bool compress, Action<IReadOnlyList<Bptr>> commit)
    {
        List<long>?[] old = [.. Arenas.Select(a => compress && a.LogBlocks >= 2 * a.CompressedBlocks ? a.Compress() : null)];
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
        for (int i = 0; i < old.Length; i++)
        {
            if (old[i] is { } blocks)
            {
                Arenas[i].FreeLog(blocks);
            }
        }
    }

    // pickarena and blkalloc: arenas taken in turn, moving on every 2048 allocations, and the next
    // tried when one is full.
    protected override (long Address, long Gen) Allocate(BlockType type)
    {
        int first = (int)((++roundRobin / 2048) % Arenas.Count);
        foreach (Arena a in Arenas.Skip(first).Concat(Arenas.Take(first)))
        {
            if (a.Allocate(useReserve: UseReserve) is { } b)
            {
                return (b, Gen);
            }
        }

        throw new GefsException("file system full");
    }

    protected override void Write(Blk b) => device.Write(b.Address, b.Buffer);
}
