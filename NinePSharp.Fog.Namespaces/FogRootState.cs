using NinePSharp.Constants;
using NinePSharp.Namespaces.Orleans;
using Orleans.Runtime;

namespace NinePSharp.Fog.Namespaces;

/// <summary>Durable entries of one shared root.</summary>
[GenerateSerializer]
public sealed class FogRootState
{
    /// <summary>Gets or sets the entries by path number; the root is 1.</summary>
    [Id(0)]
    public Dictionary<ulong, FogRootEntry> Entries { get; set; } = new();

    /// <summary>Gets or sets the last allocated path number.</summary>
    [Id(1)]
    public ulong LastPath { get; set; }
}
