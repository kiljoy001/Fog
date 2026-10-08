namespace NinePSharp.Fog.Gefs;

// gefs's Gefs: a device holding arenas and a root tree, all committed at once by its superblocks,
// the first at the device's first block and a backup at its last.
internal sealed class Store
{
    private const long B = Format.BlockSize;

    private readonly Device device;

    private Store(Device device, Allocator allocator, Tree root, long generation, long nextQid)
    {
        this.device = device;
        Allocator = allocator;
        Root = root;
        Generation = generation;
        NextQid = nextQid;
    }

    public Allocator Allocator { get; }

    public Tree Root { get; }

    // The generation last committed; blocks are born in the one after it.
    public long Generation { get; private set; }

    public long NextQid { get; private set; }

    // reamfs: arenas, and an empty root tree committed as generation 1.
    public static Store Ream(Device device, int arenas)
    {
        Allocator allocator = Allocator.Ream(device, arenas);
        allocator.Gen = 1;
        var store = new Store(device, allocator, Tree.Create(allocator), 0, 1);
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
        allocator.Gen = sb.NextGen;
        return new Store(device, allocator, new Tree(allocator, sb.Snap.Root, sb.Snap.Height), sb.SyncGen, sb.NextQid);
    }

    public long NewQid() => NextQid++;

    // sync: the blocks freed in this generation go back, and the arenas commit it by having both
    // superblocks written between their first and second headers.
    public void Commit()
    {
        Allocator.Reclaim();
        long gen = Allocator.Gen;
        Allocator.Sync(gen, true, arenas =>
        {
            var sb = new Superblock(Format.BlockSize, Format.BufferSpace, new TreeRoot(Root.Height, Root.Root), default, default, 0, NextQid, gen + 1, gen, [.. arenas]);
            var block = new byte[B];
            sb.Write(block);
            device.Write(0, block);
            device.Flush();
            device.Write(device.Size - B, block);
            device.Flush();
        });
        Generation = gen;
        Allocator.Gen = gen + 1;
    }

    private static Superblock ReadSuperblock(Device device, long offset)
    {
        var block = new byte[B];
        device.Read(offset, block);
        return Superblock.Read(block);
    }
}
