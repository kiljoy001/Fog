namespace NinePSharp.Fog.Wasm;

// A seek on a pipe, which has no offset; the call returns SPIPE.
internal sealed class PipeSeekException : Exception;
