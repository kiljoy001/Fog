namespace NinePSharp.Fog.Kernel;

internal sealed class PipeClosedException() : IOException(PipeQueue.Hungup);
