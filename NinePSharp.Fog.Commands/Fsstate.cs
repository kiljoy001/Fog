namespace NinePSharp.Fog.Commands;

// An open file's state, as dat.h's Fsstate: who opened it, where a listing has got to, the rpc
// in hand, and the conversation it carries.
public sealed class Fsstate
{
    // Phases common to the protocols; each protocol's own start at 0.
    public const int Notstarted = -3;
    public const int Broken = -2;
    public const int Established = -1;

    public SemaphoreSlim Lock { get; } = new(1, 1);

    public string Sysuser { get; set; } = string.Empty;

    public int Listoff { get; set; }

    public bool Pending { get; set; }

    // The rpc written: its verb, which of them it is, and its argument.
    public string Verb { get; set; } = string.Empty;

    public int Iverb { get; set; }

    public byte[] Arg { get; set; } = [];

    public int Nwant { get; set; }

    public string Err { get; set; } = "factotum/fs.c no error";

    public string Keyinfo { get; set; } = string.Empty;

    public string[] Phasename { get; set; } = [];

    public int Maxphase { get; set; }

    public int Phase { get; set; } = Notstarted;

    public int Seqnum { get; set; }

    public bool Haveai { get; set; }

    public Attr? Attr { get; set; }

    public AuthInfo Ai { get; set; } = new();

    public Proto? Proto { get; set; }

    public object? Ps { get; set; }

    // Pending or finished confirmations of keys.
    public List<Conf> Conf { get; set; } = [];
}
