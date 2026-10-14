using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// A block pointer: addr[8] hash[32] gen[8].
internal readonly record struct Bptr(long Addr, BlockHash Hash, long Gen)
{
    public static Bptr Read(ReadOnlySpan<byte> p) => new(
        BinaryPrimitives.ReadInt64BigEndian(p),
        BlockHash.Read(p[8..]),
        BinaryPrimitives.ReadInt64BigEndian(p[(8 + Format.HashSize)..]));

    public int Write(Span<byte> p)
    {
        BinaryPrimitives.WriteInt64BigEndian(p, Addr);
        Hash.Write(p[8..]);
        BinaryPrimitives.WriteInt64BigEndian(p[(8 + Format.HashSize)..], Gen);
        return Format.PointerSize;
    }
}
