using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// A snapshot's tree, the value of its Ksnap key.
internal sealed record TreeEntry(int Refs, int Labels, int Height, uint Flag, long Gen, long Pred, long Succ, long Base, Bptr Root)
{
    public static TreeEntry Read(ReadOnlySpan<byte> p) => new(
        BinaryPrimitives.ReadInt32BigEndian(p),
        BinaryPrimitives.ReadInt32BigEndian(p[4..]),
        BinaryPrimitives.ReadInt32BigEndian(p[8..]),
        BinaryPrimitives.ReadUInt32BigEndian(p[12..]),
        BinaryPrimitives.ReadInt64BigEndian(p[16..]),
        BinaryPrimitives.ReadInt64BigEndian(p[24..]),
        BinaryPrimitives.ReadInt64BigEndian(p[32..]),
        BinaryPrimitives.ReadInt64BigEndian(p[40..]),
        Bptr.Read(p[48..]));

    public byte[] Value()
    {
        var p = new byte[Format.TreeSize];
        BinaryPrimitives.WriteInt32BigEndian(p, Refs);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(4), Labels);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(8), Height);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), Flag);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(16), Gen);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(24), Pred);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(32), Succ);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(40), Base);
        Root.Write(p.AsSpan(48));
        return p;
    }
}
