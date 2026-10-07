using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// retag2kv's value: link[8] labels[1] refs[1], the counts signed.
internal static class SnapshotChange
{
    public static byte[] Pack(long link, sbyte labels, sbyte refs)
    {
        var p = new byte[8 + 1 + 1];
        BinaryPrimitives.WriteInt64BigEndian(p, link);
        p[8] = (byte)labels;
        p[9] = (byte)refs;
        return p;
    }
}
