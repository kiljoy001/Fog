namespace NinePSharp.Fog.Commands;

// authsrv.h's Nvrsafe, as nvram holds it: the machine's DES key, the authid's, the secstore key,
// the authid and its domain, and the machine's AES key, each with its nvcsum.
public sealed class Nvrsafe
{
    public const int Size = Authsrv.Deskeylen + 1 + Authsrv.Deskeylen + 1 + Authsrv.Configlen + 1 + Authsrv.Anamelen + 1 + Authsrv.Domlen + 1 + Authsrv.Aeskeylen + 1;

    public byte[] Machkey { get; } = new byte[Authsrv.Deskeylen];

    public byte Machsum { get; set; }

    public byte[] Authkey { get; } = new byte[Authsrv.Deskeylen];

    public byte Authsum { get; set; }

    public byte[] Config { get; } = new byte[Authsrv.Configlen];

    public byte Configsum { get; set; }

    public byte[] Authid { get; } = new byte[Authsrv.Anamelen];

    public byte Authidsum { get; set; }

    public byte[] Authdom { get; } = new byte[Authsrv.Domlen];

    public byte Authdomsum { get; set; }

    public byte[] Aesmachkey { get; } = new byte[Authsrv.Aeskeylen];

    public byte Aesmachsum { get; set; }

    // The Nvrsafe at the start of b, what b lacks of it zero.
    public static Nvrsafe FromBytes(ReadOnlySpan<byte> bytes)
    {
        byte[] b = new byte[Size];
        bytes[..Math.Min(bytes.Length, Size)].CopyTo(b);
        var safe = new Nvrsafe();
        int o = 0;
        Take(b, ref o, safe.Machkey);
        safe.Machsum = b[o++];
        Take(b, ref o, safe.Authkey);
        safe.Authsum = b[o++];
        Take(b, ref o, safe.Config);
        safe.Configsum = b[o++];
        Take(b, ref o, safe.Authid);
        safe.Authidsum = b[o++];
        Take(b, ref o, safe.Authdom);
        safe.Authdomsum = b[o++];
        Take(b, ref o, safe.Aesmachkey);
        safe.Aesmachsum = b[o];
        return safe;
    }

    public byte[] ToBytes() =>
        [.. Machkey, Machsum, .. Authkey, Authsum, .. Config, Configsum, .. Authid, Authidsum, .. Authdom, Authdomsum, .. Aesmachkey, Aesmachsum];

    // memset of the whole: every field and sum to zero.
    public void Clear() => FromBytes(new byte[Size]).CopyTo(this);

    public void CopyTo(Nvrsafe to)
    {
        Machkey.CopyTo(to.Machkey, 0);
        Authkey.CopyTo(to.Authkey, 0);
        Config.CopyTo(to.Config, 0);
        Authid.CopyTo(to.Authid, 0);
        Authdom.CopyTo(to.Authdom, 0);
        Aesmachkey.CopyTo(to.Aesmachkey, 0);
        (to.Machsum, to.Authsum, to.Configsum, to.Authidsum, to.Authdomsum, to.Aesmachsum) = (Machsum, Authsum, Configsum, Authidsum, Authdomsum, Aesmachsum);
    }

    private static void Take(ReadOnlySpan<byte> b, ref int o, byte[] field)
    {
        b.Slice(o, field.Length).CopyTo(field);
        o += field.Length;
    }
}
