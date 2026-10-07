namespace NinePSharp.Fog.Gefs;

// dat.h's sizes, with Fog's SHA-256 block pointers.
internal static class Format
{
    public const int BlockShift = 14;
    public const int BlockSize = 1 << BlockShift;
    public const int MaxEntry = 256;
    public const int MaxName = MaxEntry - 1 - 9 - 1;
    public const int HashSize = 32;
    public const int PointerSize = 8 + HashSize + 8;
    public const int DirSize = 8 + 8 + 4 + 1 + 4 + 8 + 8 + 8 + 4 + 4 + 4;
    public const int TreeSize = 4 + 4 + 4 + 4 + 8 + 8 + 8 + 8 + PointerSize;
    public const int PivotHeaderSize = 10;
    public const int BufferSpace = (BlockSize - PivotHeaderSize) / 2;
    public const int MaxArenas = 256;

    // Not "gefs9.00": the pointers differ, so 9front's gefs must not open a Fog store, nor Fog a gefs one.
    public static ReadOnlySpan<byte> Version => "fogfs001"u8;
}
