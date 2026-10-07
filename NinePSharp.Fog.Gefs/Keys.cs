using System.Buffers.Binary;
using System.Text;

namespace NinePSharp.Fog.Gefs;

internal static class Keys
{
    // keycmp: by bytes, then a key before any longer key it begins.
    public static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Math.Sign(a.SequenceCompareTo(b));

    // Kent pqid[8] name: packdkey, with packstr's length and terminator.
    public static byte[] Entry(long parent, string name)
    {
        int length = Encoding.UTF8.GetByteCount(name);
        if (length > Format.MaxName)
        {
            throw new GefsException("name too long");
        }

        var key = new byte[1 + 8 + 2 + length + 1];
        key[0] = (byte)KeyType.Entry;
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(1), parent);
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(9), (ushort)length);
        Encoding.UTF8.GetBytes(name, key.AsSpan(11));
        return key;
    }

    public static string ReadEntry(ReadOnlySpan<byte> key, out long parent)
    {
        parent = BinaryPrimitives.ReadInt64BigEndian(key[1..]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(key[9..]);
        return Encoding.UTF8.GetString(key.Slice(11, length));
    }

    // Ksnap snapid[8].
    public static byte[] Snap(long id)
    {
        var key = new byte[1 + 8];
        key[0] = (byte)KeyType.Snap;
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(1), id);
        return key;
    }
}
