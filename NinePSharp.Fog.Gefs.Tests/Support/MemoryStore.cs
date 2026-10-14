namespace NinePSharp.Fog.Gefs.Tests.Support;

// Blocks kept in memory, with every allocation and free recorded so a test can see what leaked.
internal sealed class MemoryStore : BlockStore
{
    private readonly Dictionary<long, byte[]> written = [];
    private long next = Format.BlockSize;

    public HashSet<long> Live { get; } = [];

    public List<long> Freed { get; } = [];

    public int Allocations { get; private set; }

    public override Blk Get(Bptr bp)
        => Live.Contains(bp.Addr) ? Blk.Read(written[bp.Addr], bp) : throw new InvalidOperationException($"block {bp.Addr} read after it was freed");

    public override void Free(Tree t, Bptr bp) => Free(bp);

    public void Free(Bptr bp)
    {
        if (!Live.Remove(bp.Addr))
        {
            throw new InvalidOperationException($"block {bp.Addr} freed twice");
        }

        written.Remove(bp.Addr);
        Freed.Add(bp.Addr);
    }

    protected override long Allocate(BlockType type)
    {
        long address = next;
        next += Format.BlockSize;
        Live.Add(address);
        Allocations++;
        return address;
    }

    protected override void Write(Blk b) => written[b.Address] = b.Buffer.ToArray();
}
