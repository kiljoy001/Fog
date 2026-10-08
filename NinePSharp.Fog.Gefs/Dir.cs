using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// An Xdir: the name is in its Kent key, the rest packed in its value. Unlike gefs, which keeps
// numeric ids from a user table of its own, Fog's entries name their owner, group and last
// modifier, as Fog's users come from its auth server.
internal sealed record Dir(string Name, Qid Qid, uint Mode, long Atime, long Mtime, long Length, string User, string Group, string Muid, long Flag = 0)
{
    public const int FixedSize = 8 + 8 + 4 + 1 + 4 + 8 + 8 + 8;

    public static Dir Read(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        string name = Keys.ReadEntry(key, out _);
        ReadOnlySpan<byte> names = value[FixedSize..];
        return new Dir(
            name,
            new Qid(BinaryPrimitives.ReadInt64BigEndian(value[8..]), BinaryPrimitives.ReadUInt32BigEndian(value[16..]), value[20]),
            BinaryPrimitives.ReadUInt32BigEndian(value[21..]),
            BinaryPrimitives.ReadInt64BigEndian(value[25..]),
            BinaryPrimitives.ReadInt64BigEndian(value[33..]),
            BinaryPrimitives.ReadInt64BigEndian(value[41..]),
            Names.Read(ref names),
            Names.Read(ref names),
            Names.Read(ref names),
            BinaryPrimitives.ReadInt64BigEndian(value));
    }

    public byte[] Key(long parent) => Keys.Entry(parent, Name);

    public byte[] Value()
    {
        var p = new byte[FixedSize];
        BinaryPrimitives.WriteInt64BigEndian(p, Flag);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(8), Qid.Path);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), Qid.Version);
        p[20] = Qid.Type;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(21), Mode);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(25), Atime);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(33), Mtime);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(41), Length);
        return [.. p, .. Names.Pack(User), .. Names.Pack(Group), .. Names.Pack(Muid)];
    }
}
