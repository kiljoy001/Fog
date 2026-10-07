namespace NinePSharp.Fog.Gefs;

// A wstat message's flags, followed by each set field's value in this order.
[Flags]
internal enum WstatFields : byte
{
    None = 0,
    Size = 1 << 0,
    Mode = 1 << 1,
    Mtime = 1 << 2,
    Atime = 1 << 3,
    Uid = 1 << 4,
    Gid = 1 << 5,
    Muid = 1 << 6,
}
