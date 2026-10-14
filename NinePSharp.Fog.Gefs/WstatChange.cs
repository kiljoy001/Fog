using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// The value of a wstat message: its flags, then each set field in flag order, names as Names packs them.
internal sealed record WstatChange(long? Length = null, uint? Mode = null, long? Mtime = null, long? Atime = null, string? User = null, string? Group = null, string? Muid = null)
{
    public byte[] Pack()
    {
        WstatFields fields = (Length is null ? WstatFields.None : WstatFields.Size)
            | (Mode is null ? WstatFields.None : WstatFields.Mode)
            | (Mtime is null ? WstatFields.None : WstatFields.Mtime)
            | (Atime is null ? WstatFields.None : WstatFields.Atime)
            | (User is null ? WstatFields.None : WstatFields.Uid)
            | (Group is null ? WstatFields.None : WstatFields.Gid)
            | (Muid is null ? WstatFields.None : WstatFields.Muid);
        var p = new List<byte>();
        Add(Length, 8, (b, v) => BinaryPrimitives.WriteInt64BigEndian(b, v));
        Add(Mode, 4, (b, v) => BinaryPrimitives.WriteUInt32BigEndian(b, v));
        Add(Mtime, 8, (b, v) => BinaryPrimitives.WriteInt64BigEndian(b, v));
        Add(Atime, 8, (b, v) => BinaryPrimitives.WriteInt64BigEndian(b, v));
        foreach (string? name in (string?[])[User, Group, Muid])
        {
            if (name is not null)
            {
                p.AddRange(Names.Pack(name));
            }
        }

        return [(byte)fields, .. p];

        void Add<T>(T? value, int size, Action<Span<byte>, T> write)
            where T : struct
        {
            if (value is { } set)
            {
                var bytes = new byte[size];
                write(bytes, set);
                p.AddRange(bytes);
            }
        }
    }
}
