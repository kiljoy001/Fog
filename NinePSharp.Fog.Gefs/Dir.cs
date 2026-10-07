using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// An Xdir: the name is in its Kent key, the rest packed in its value.
internal sealed record Dir(string Name, Qid Qid, uint Mode, long Atime, long Mtime, long Length, int Uid, int Gid, int Muid, long Flag = 0)
{
    public static Dir Read(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        string name = Keys.ReadEntry(key, out _);
        return new Dir(
            name,
            new Qid(BinaryPrimitives.ReadInt64BigEndian(value[8..]), BinaryPrimitives.ReadUInt32BigEndian(value[16..]), value[20]),
            BinaryPrimitives.ReadUInt32BigEndian(value[21..]),
            BinaryPrimitives.ReadInt64BigEndian(value[25..]),
            BinaryPrimitives.ReadInt64BigEndian(value[33..]),
            BinaryPrimitives.ReadInt64BigEndian(value[41..]),
            BinaryPrimitives.ReadInt32BigEndian(value[49..]),
            BinaryPrimitives.ReadInt32BigEndian(value[53..]),
            BinaryPrimitives.ReadInt32BigEndian(value[57..]),
            BinaryPrimitives.ReadInt64BigEndian(value));
    }

    public byte[] Key(long parent) => Keys.Entry(parent, Name);

    public byte[] Value()
    {
        var p = new byte[Format.DirSize];
        BinaryPrimitives.WriteInt64BigEndian(p, Flag);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(8), Qid.Path);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), Qid.Version);
        p[20] = Qid.Type;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(21), Mode);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(25), Atime);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(33), Mtime);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(41), Length);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(49), Uid);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(53), Gid);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(57), Muid);
        return p;
    }
}
