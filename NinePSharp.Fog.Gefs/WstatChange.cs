using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// The value of a wstat message: its flags, then each set field in flag order.
internal sealed record WstatChange(long? Length = null, uint? Mode = null, long? Mtime = null, long? Atime = null, int? Uid = null, int? Gid = null, int? Muid = null)
{
    public byte[] Pack()
    {
        WstatFields fields = (Length is null ? WstatFields.None : WstatFields.Size)
            | (Mode is null ? WstatFields.None : WstatFields.Mode)
            | (Mtime is null ? WstatFields.None : WstatFields.Mtime)
            | (Atime is null ? WstatFields.None : WstatFields.Atime)
            | (Uid is null ? WstatFields.None : WstatFields.Uid)
            | (Gid is null ? WstatFields.None : WstatFields.Gid)
            | (Muid is null ? WstatFields.None : WstatFields.Muid);
        var p = new List<byte>();
        Add(Length, 8, (b, v) => BinaryPrimitives.WriteInt64BigEndian(b, v));
        Add(Mode, 4, (b, v) => BinaryPrimitives.WriteUInt32BigEndian(b, v));
        Add(Mtime, 8, (b, v) => BinaryPrimitives.WriteInt64BigEndian(b, v));
        Add(Atime, 8, (b, v) => BinaryPrimitives.WriteInt64BigEndian(b, v));
        Add(Uid, 4, (b, v) => BinaryPrimitives.WriteInt32BigEndian(b, v));
        Add(Gid, 4, (b, v) => BinaryPrimitives.WriteInt32BigEndian(b, v));
        Add(Muid, 4, (b, v) => BinaryPrimitives.WriteInt32BigEndian(b, v));
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
