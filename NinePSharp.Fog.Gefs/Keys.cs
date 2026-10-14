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

    // Klabel name[]: a snapshot's name.
    public static byte[] Label(string name)
    {
        int length = Encoding.UTF8.GetByteCount(name);
        if (length > Format.MaxName)
        {
            throw new GefsException("name too long");
        }

        var key = new byte[1 + length];
        key[0] = (byte)KeyType.Label;
        Encoding.UTF8.GetBytes(name, key.AsSpan(1));
        return key;
    }

    // Kdlist snapid[8] birth[8]: the deadlist of blocks a snapshot freed that were born in one generation.
    public static byte[] Deadlist(long gen, long birth)
    {
        var key = new byte[1 + 8 + 8];
        key[0] = (byte)KeyType.Deadlist;
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(1), gen);
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(9), birth);
        return key;
    }

    // Kup qid[8]: the key of an entry's directory entry, so its handle finds it.
    public static byte[] Up(long qid)
    {
        var key = new byte[1 + 8];
        key[0] = (byte)KeyType.Up;
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(1), qid);
        return key;
    }

    // Kdat qid[8] offset[8]: the data block holding a file's bytes from a block-aligned offset.
    public static byte[] Data(long qid, long offset)
    {
        var key = new byte[1 + 8 + 8];
        key[0] = (byte)KeyType.Data;
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(1), qid);
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(9), offset);
        return key;
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
