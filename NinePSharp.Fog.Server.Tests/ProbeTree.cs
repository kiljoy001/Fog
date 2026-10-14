using NinePSharp.Fog.Server;

namespace NinePSharp.Fog.Server.Tests;

// A control tree whose walks, listings and opens a test sets directly.
internal sealed class ProbeTree : FogFileTree
{
    public override FogFileNode Root { get; } = new(1, "/", true);

    internal FogFileNode Walked { get; set; } = new(2, "file", false);

    internal IReadOnlyList<FogFileNode>? Listed { get; set; }

    internal Func<FogOpenFile> OnOpen { get; set; } = () => new();

    internal HashSet<string> RejectedNames { get; } = [];

    internal List<string> ClosedSessions { get; } = new();

    internal Exception? CloseFailure { get; set; }

    public override FogFileNode Walk(FogPrincipal principal, FogFileNode directory, string name) =>
        RejectedNames.Contains(name) ? throw new FogException("tx-expired") : name == "." ? directory : Walked;

    public override IReadOnlyList<FogFileNode> List(FogPrincipal principal, FogFileNode directory) => Listed ?? [Walked];

    public override void Check(FogPrincipal principal, FogFileNode node)
    {
    }

    public override FogOpenFile Open(FogPrincipal principal, string session, FogFileNode node, byte mode, long snapshotBudget) => OnOpen();

    public override void CloseSession(string session)
    {
        // The dispatcher closes sessions from many tasks at once.
        lock (ClosedSessions)
        {
            ClosedSessions.Add(session);
        }

        if (CloseFailure is not null)
        {
            throw CloseFailure;
        }
    }
}
