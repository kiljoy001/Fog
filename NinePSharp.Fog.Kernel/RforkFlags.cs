namespace NinePSharp.Fog.Kernel;

[Flags]
public enum RforkFlags
{
    Nameg = 1 << 0,
    Envg = 1 << 1,
    Fdg = 1 << 2,
    Noteg = 1 << 3,
    Proc = 1 << 4,
    Mem = 1 << 5,
    Nowait = 1 << 6,
    Cnameg = 1 << 10,
    Cenvg = 1 << 11,
    Cfdg = 1 << 12,
    Rend = 1 << 13,
    Nomnt = 1 << 14,
}
