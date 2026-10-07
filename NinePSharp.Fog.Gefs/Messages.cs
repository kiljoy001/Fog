using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

internal static class Messages
{
    // tree.c's apply: the value a message leaves at its key, or null if it leaves none.
    public static byte[]? Apply(byte[] value, Message message) => message.Op switch
    {
        MessageOp.Insert => message.Value,
        MessageOp.Delete or MessageOp.ClearBlock or MessageOp.Clobber => null,
        MessageOp.Wstat => Stat(value, message),
        MessageOp.Relink or MessageOp.Reprev or MessageOp.Incref => Retag(value, message),
        _ => throw new GefsException("invalid message"),
    };

    // statupdate: every wstat bumps the qid version, and a new mode's top byte is the qid type.
    private static byte[] Stat(byte[] value, Message message)
    {
        Dir d = Dir.Read(message.Key, value);
        ReadOnlySpan<byte> p = message.Value;
        var fields = (WstatFields)p[0];
        p = p[1..];
        d = d with { Qid = d.Qid with { Version = d.Qid.Version + 1 } };
        if (fields.HasFlag(WstatFields.Size))
        {
            d = d with { Length = BinaryPrimitives.ReadInt64BigEndian(p) };
            p = p[8..];
        }

        if (fields.HasFlag(WstatFields.Mode))
        {
            d = d with { Mode = BinaryPrimitives.ReadUInt32BigEndian(p), Qid = d.Qid with { Type = p[0] } };
            p = p[4..];
        }

        if (fields.HasFlag(WstatFields.Mtime))
        {
            d = d with { Mtime = BinaryPrimitives.ReadInt64BigEndian(p) };
            p = p[8..];
        }

        if (fields.HasFlag(WstatFields.Atime))
        {
            d = d with { Atime = BinaryPrimitives.ReadInt64BigEndian(p) };
            p = p[8..];
        }

        if (fields.HasFlag(WstatFields.Uid))
        {
            d = d with { Uid = BinaryPrimitives.ReadInt32BigEndian(p) };
            p = p[4..];
        }

        if (fields.HasFlag(WstatFields.Gid))
        {
            d = d with { Gid = BinaryPrimitives.ReadInt32BigEndian(p) };
            p = p[4..];
        }

        if (fields.HasFlag(WstatFields.Muid))
        {
            d = d with { Muid = BinaryPrimitives.ReadInt32BigEndian(p) };
            p = p[4..];
        }

        return p.IsEmpty ? d.Value() : throw new GefsException("malformed stat");
    }

    private static byte[] Retag(byte[] value, Message message)
    {
        TreeEntry t = TreeEntry.Read(value);
        ReadOnlySpan<byte> p = message.Value;
        long link = BinaryPrimitives.ReadInt64BigEndian(p);
        t = message.Op switch
        {
            MessageOp.Relink => t with { Succ = link },
            MessageOp.Reprev => t with { Pred = link },
            _ => t,
        };
        t = t with { Labels = t.Labels + (sbyte)p[8], Refs = t.Refs + (sbyte)p[9] };
        return t.Labels >= 0 && t.Refs >= 0 ? t.Value() : throw new GefsException("negative snapshot count");
    }
}
