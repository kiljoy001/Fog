namespace NinePSharp.Fog.Wasm;

// Preview 1's errno values, as typenames.witx numbers them.
internal enum Errno
{
    Success = 0,
    Badf = 8,
    Fault = 21,
    Io = 29,
    Nosys = 52,
}
