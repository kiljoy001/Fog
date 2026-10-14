namespace NinePSharp.Fog.Gefs;

// The blocks a snapshot freed that were born in one generation, kept while an older snapshot may
// still use them: a chain of blocks, each naming the one written before it, and the block being
// filled, written at the next commit.
internal sealed class Deadlist(long gen, long birth, Bptr head)
{
    public long Gen => gen;

    public long Birth => birth;

    public Bptr Head { get; set; } = head;

    public Blk? Ins { get; set; }
}
