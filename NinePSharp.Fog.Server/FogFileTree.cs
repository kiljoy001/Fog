using NinePSharp.Constants;

namespace NinePSharp.Fog.Server;

public abstract class FogFileTree
{
    private long nextQid;

    protected ulong AllocateQid()
    {
        return (ulong)Interlocked.Increment(ref nextQid);
    }

    public abstract FogFileNode Root { get; }
    public abstract FogFileNode Walk(FogPrincipal principal, FogFileNode directory, string name);
    public abstract IReadOnlyList<FogFileNode> List(FogPrincipal principal, FogFileNode directory);
    public abstract void Check(FogPrincipal principal, FogFileNode node);
    public abstract FogOpenFile Open(FogPrincipal principal, string session, FogFileNode node, byte mode, long snapshotBudget);
    public abstract void CloseSession(string session);
}

public sealed record FogFileNode(ulong QidPath, string Name, bool Directory, string Service = "", string Transaction = "", string File = "");

/// <summary>One fid's opening. Snapshot bytes are reserved separately by the dispatcher.</summary>
public sealed class FogOpenFile : IDisposable
{
    private readonly Action<bool>? close;
    public FogOpenFile(byte[]? snapshot = null,
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
