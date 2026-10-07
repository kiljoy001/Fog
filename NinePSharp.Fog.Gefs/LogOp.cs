namespace NinePSharp.Fog.Gefs;

// An allocation log entry's op, in the low byte of its offset; range ops take a second word.
internal enum LogOp : byte
{
    Nop,
    Alloc1,
    Free1,
    Sync,
    Alloc,
    Free,
}
