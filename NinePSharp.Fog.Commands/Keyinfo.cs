namespace NinePSharp.Fog.Commands;

// findkey's question, as dat.h's Keyinfo: whose state, which user, and which keys count.
public sealed class Keyinfo(Fsstate fss, Attr? attr)
{
    public Fsstate Fss { get; } = fss;

    public string? User { get; set; } = fss.Sysuser;

    public bool Noconf { get; set; }

    public int Skip { get; set; }

    public bool Usedisabled { get; set; }

    public Attr? Attr { get; set; } = attr ?? fss.Attr;
}
