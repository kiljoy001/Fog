namespace NinePSharp.Fog.Wasm;

// Preview 1's errno values, as typenames.witx numbers them.
internal enum Errno
{
    Success = 0,
    Badf = 8,
    Fault = 21,
    Inval = 28,
    Io = 29,
    Isdir = 31,
    Nosys = 52,
}
