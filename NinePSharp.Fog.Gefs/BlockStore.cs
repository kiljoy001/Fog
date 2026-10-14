namespace NinePSharp.Fog.Gefs;

// What the tree needs of blk.c: newblk, dupblk, enqueue, getblk and freeblk. Where blocks are
// allocated, and whether a freed block is reused or kept for a snapshot, is the store's affair.
internal abstract class BlockStore
{
    // A block born in a generation: the one its tree is writing.
    public Blk New(BlockType type, long gen = 0) => Blk.New(type, Allocate(type), gen);

    public Blk Duplicate(Blk b, long gen = 0) => b.Copy(Allocate(b.Type), gen);

    public void Enqueue(Blk b)
    {
        b.Seal();
        Write(b);
    }

    public abstract Blk Get(Bptr bp);

    // A block a tree no longer uses; the tree says whether a snapshot may still need it.
    public abstract void Free(Tree t, Bptr bp);

    protected abstract long Allocate(BlockType type);

    protected abstract void Write(Blk b);
}
