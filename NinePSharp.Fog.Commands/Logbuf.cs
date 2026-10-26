namespace NinePSharp.Fog.Commands;

// A log buffer, as dat.h's Logbuf: the last 128 messages, and the reads waiting for one.
public sealed class Logbuf
{
    public object Lock { get; } = new();

    public Queue<Req> Wait { get; } = new();

    public string?[] Msg { get; } = new string?[128];

    public int Rp { get; set; }

    public int Wp { get; set; }
}
