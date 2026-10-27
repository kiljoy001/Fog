using NinePSharp.Constants;

namespace NinePSharp.Fog.Commands;

// A fid, as fid.c keeps it: its number, the mode it was opened in, or -1, and what the server keeps.
public sealed class Fid(uint number)
{
    public uint Number { get; } = number;

    public int Omode { get; set; } = -1;

    public Qid Qid { get; set; }

    // Stryker disable once String : srv asks a fid for its user only once attach or a walk's clone has given it one
    public string Uid { get; set; } = string.Empty;

    public object? Aux { get; set; }

    public ulong Diroffset { get; set; }

    public int Dirindex { get; set; }

    internal int Ref { get; set; }
}
