using System.Collections.Frozen;
using System.Text;
using NinePSharp.Constants;

namespace NinePSharp.Fog.Server;

/// <summary>A host-local registration. Its commit callback must validate operation-specific required inputs.</summary>
public sealed record FogTransactionService(
    string Name,
    FogTransactionStore Store,
    IReadOnlySet<string> InputFiles,
    IReadOnlySet<string> OutputFiles,
    Func<FogPrincipal, string, CancellationToken, Task> Commit);
