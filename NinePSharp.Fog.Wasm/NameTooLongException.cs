namespace NinePSharp.Fog.Wasm;

// A name longer than the buffer the application gave for it; the call returns NAMETOOLONG.
internal sealed class NameTooLongException : Exception;
