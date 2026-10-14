using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NinePSharp.Fog.Gefs;

internal readonly record struct BlockHash(ulong A, ulong B, ulong C, ulong D)
{
    public static BlockHash Of(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[Format.HashSize];
        SHA256.HashData(data, hash);
        return Read(hash);
    }

    public static BlockHash Read(ReadOnlySpan<byte> p) => new(
        BinaryPrimitives.ReadUInt64BigEndian(p),
        BinaryPrimitives.ReadUInt64BigEndian(p[8..]),
        BinaryPrimitives.ReadUInt64BigEndian(p[16..]),
        BinaryPrimitives.ReadUInt64BigEndian(p[24..]));

    public void Write(Span<byte> p)
    {
        BinaryPrimitives.WriteUInt64BigEndian(p, A);
        BinaryPrimitives.WriteUInt64BigEndian(p[8..], B);
        BinaryPrimitives.WriteUInt64BigEndian(p[16..], C);
        BinaryPrimitives.WriteUInt64BigEndian(p[24..], D);
    }
}
