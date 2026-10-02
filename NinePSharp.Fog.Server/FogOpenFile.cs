using NinePSharp.Constants;

namespace NinePSharp.Fog.Server;

/// <summary>One fid's opening. Snapshot bytes are reserved separately by the dispatcher.</summary>
public sealed class FogOpenFile : IDisposable
{
    private readonly Action<bool>? close;

    public FogOpenFile(
        byte[]? snapshot = null,
        Func<ulong, ReadOnlyMemory<byte>, CancellationToken, Task<uint>>? write = null,
        Action<bool>? close = null)
    {
        Snapshot = snapshot;
        Write = write;
        this.close = close;
    }

    public byte[]? Snapshot { get; }

    public Func<ulong, ReadOnlyMemory<byte>, CancellationToken, Task<uint>>? Write { get; }

    public void Clunk() => close?.Invoke(true);

    public void Dispose() => close?.Invoke(false);
}
