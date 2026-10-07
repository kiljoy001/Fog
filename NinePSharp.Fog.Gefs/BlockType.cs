namespace NinePSharp.Fog.Gefs;

internal enum BlockType : ushort
{
    Data,
    Pivot,
    Leaf,
    Log,
    Deadlist,
    Arena,

    // gefs's superblock tag is "ge", the start of its "gefs9.00"; Fog's is "fo", the start of "fogfs001".
    Super = 0x666f,
}
