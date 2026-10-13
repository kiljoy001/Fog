namespace NinePSharp.Fog.Wasm;

// Preview 1's errno values, as typenames.witx numbers them.
internal enum Errno
{
    Success = 0,
    Acces = 2,
    Badf = 8,
    Exist = 20,
    Fault = 21,
    Ilseq = 25,
    Inval = 28,
    Io = 29,
    Isdir = 31,
    Nametoolong = 37,
    Noent = 44,
    Notdir = 54,
    Notempty = 55,
    Nosys = 52,
    Notsock = 57,
    Notsup = 58,
    Spipe = 70,
    Notcapable = 76,
}
