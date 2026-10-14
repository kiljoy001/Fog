namespace NinePSharp.Fog.Gefs;

// An allocation log entry's op, in the low byte of its offset. Only a range of free blocks takes a
// second word, for its length; Fog allocates one block at a time, so it logs no allocated ranges.
internal enum LogOp : byte
{
    Nop,
    Alloc1,
    Free1,
    Sync,
    Free = 5,
}
