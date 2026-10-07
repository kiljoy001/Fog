namespace NinePSharp.Fog.Gefs;

internal enum MessageOp : byte
{
    Nop,
    Insert,
    Delete,
    ClearBlock,
    Clobber,
    Wstat,
    Relink,
    Reprev,
    Incref,
}
