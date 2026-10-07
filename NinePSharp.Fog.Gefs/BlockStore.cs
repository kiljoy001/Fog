namespace NinePSharp.Fog.Gefs;

// What the tree needs of blk.c: newblk, dupblk, enqueue, getblk and freebp. Where blocks are
// allocated, and whether a freed block is reused or kept for a snapshot, is the store's affair.
internal abstract class BlockStore
{
    public Blk New(BlockType type)
    {
        var (address, gen) = Allocate(type);
        return Blk.New(type, address, gen);
    }

    public Blk Duplicate(Blk b)
    {
        var (address, gen) = Allocate(b.Type);
        return b.Copy(address, gen);
    }

    public void Enqueue(Blk b)
    {
        b.Seal();
        Write(b);
    }

    public abstract Blk Get(Bptr bp);

    public abstract void Free(Bptr bp);

    protected abstract (long Address, long Gen) Allocate(BlockType type);

    protected abstract void Write(Blk b);
}
