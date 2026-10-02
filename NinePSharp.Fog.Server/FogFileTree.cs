using NinePSharp.Constants;

namespace NinePSharp.Fog.Server;

public abstract class FogFileTree
{
    private long nextQid;

    public abstract FogFileNode Root { get; }

    public abstract FogFileNode Walk(FogPrincipal principal, FogFileNode directory, string name);

    public abstract IReadOnlyList<FogFileNode> List(FogPrincipal principal, FogFileNode directory);

    public abstract void Check(FogPrincipal principal, FogFileNode node);

    public abstract FogOpenFile Open(FogPrincipal principal, string session, FogFileNode node, byte mode, long snapshotBudget);

    public abstract void CloseSession(string session);

    protected ulong AllocateQid()
    {
        return (ulong)Interlocked.Increment(ref nextQid);
    }
}
