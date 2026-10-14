using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// packsb's layout, with SHA-256 hashes. Pointers in the superblock carry no generation.
internal sealed record Superblock(
    int BlockSize,
    int BufferSpace,
    TreeRoot Snap,
    Bptr DeadlistHead,
    Bptr DeadlistTail,
    long Flag,
    long NextQid,
    long NextGen,
    long SyncGen,
    Bptr[] Arenas)
{
    private const int Fixed = 8 + 4 + 4 + 4 + 4 + (3 * (8 + Format.HashSize)) + 8 + 8 + 8 + 8;

    public static Superblock Read(ReadOnlySpan<byte> block)
    {
        if (!block.StartsWith(Format.Version))
        {
            throw new GefsException("unknown fs version");
        }

        int arenas = BinaryPrimitives.ReadInt32BigEndian(block[16..]);
        int end = Fixed + (arenas * (8 + Format.HashSize));
        if (arenas is < 0 or > Format.MaxArenas || BlockHash.Read(block[end..]) != BlockHash.Of(block[..end]))
        {
            throw new GefsException("corrupt superblock");
        }

        var arenaPointers = new Bptr[arenas];
        for (int i = 0; i < arenas; i++)
        {
            arenaPointers[i] = Pointer(block[(Fixed + (i * (8 + Format.HashSize)))..]);
        }

        return new Superblock(
            BinaryPrimitives.ReadInt32BigEndian(block[8..]),
            BinaryPrimitives.ReadInt32BigEndian(block[12..]),
            new TreeRoot(BinaryPrimitives.ReadInt32BigEndian(block[20..]), Pointer(block[24..])),
            Pointer(block[64..]),
            Pointer(block[104..]),
            BinaryPrimitives.ReadInt64BigEndian(block[144..]),
            BinaryPrimitives.ReadInt64BigEndian(block[152..]),
            BinaryPrimitives.ReadInt64BigEndian(block[160..]),
            BinaryPrimitives.ReadInt64BigEndian(block[168..]),
            arenaPointers);
    }

    public void Write(Span<byte> block)
    {
        Format.Version.CopyTo(block);
        BinaryPrimitives.WriteInt32BigEndian(block[8..], BlockSize);
        BinaryPrimitives.WriteInt32BigEndian(block[12..], BufferSpace);
        BinaryPrimitives.WriteInt32BigEndian(block[16..], Arenas.Length);
        BinaryPrimitives.WriteInt32BigEndian(block[20..], Snap.Height);
        WritePointer(block[24..], Snap.Root);
        WritePointer(block[64..], DeadlistHead);
        WritePointer(block[104..], DeadlistTail);
        BinaryPrimitives.WriteInt64BigEndian(block[144..], Flag);
        BinaryPrimitives.WriteInt64BigEndian(block[152..], NextQid);
        BinaryPrimitives.WriteInt64BigEndian(block[160..], NextGen);
        BinaryPrimitives.WriteInt64BigEndian(block[168..], SyncGen);
        int p = Fixed;
        foreach (Bptr arena in Arenas)
        {
            WritePointer(block[p..], arena);
            p += 8 + Format.HashSize;
        }

        BlockHash.Of(block[..p]).Write(block[p..]);
    }

    private static Bptr Pointer(ReadOnlySpan<byte> p) => new(BinaryPrimitives.ReadInt64BigEndian(p), BlockHash.Read(p[8..]), -1);

    private static void WritePointer(Span<byte> p, Bptr bp)
    {
        BinaryPrimitives.WriteInt64BigEndian(p, bp.Addr);
        bp.Hash.Write(p[8..]);
    }
}
