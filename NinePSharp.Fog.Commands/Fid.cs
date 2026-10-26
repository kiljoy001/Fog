using NinePSharp.Constants;

namespace NinePSharp.Fog.Commands;

// A fid, as fid.c keeps it: its number, the mode it was opened in, or -1, and what the server keeps.
public sealed class Fid(uint number)
{
    public uint Number { get; } = number;

    public int Omode { get; set; } = -1;

    public Qid Qid { get; set; }

    public string Uid { get; set; } = string.Empty;

    public object? Aux { get; set; }

    public ulong Diroffset { get; set; }

    public int Dirindex { get; set; }

    internal int Ref { get; set; }
}
