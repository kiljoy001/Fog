using System.Buffers.Binary;
using System.Text;

namespace NinePSharp.Fog.Gefs;

// A name as gefs's packstr writes it, less the terminator: its length, then its bytes.
internal static class Names
{
    public static byte[] Pack(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > Format.MaxName)
        {
            throw new GefsException("name too long");
        }

        var packed = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packed, (ushort)bytes.Length);
        bytes.CopyTo(packed, 2);
        return packed;
    }

    public static string Read(ref ReadOnlySpan<byte> p)
    {
        int length = BinaryPrimitives.ReadUInt16BigEndian(p);
        string name = Encoding.UTF8.GetString(p.Slice(2, length));
        p = p[(2 + length)..];
        return name;
    }
}
