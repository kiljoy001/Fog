namespace NinePSharp.Fog.Gefs;

// getblk's GBraw and GBnochk.
[Flags]
internal enum ReadFlags
{
    None = 0,
    Raw = 1 << 0,
    NoCheck = 1 << 2,
}
