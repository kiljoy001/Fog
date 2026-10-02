using NinePSharp.Constants;
using NinePSharp.Namespaces.Orleans;
using Orleans.Runtime;

namespace NinePSharp.Fog.Namespaces;

/// <summary>One durable directory or file of the shared root.</summary>
[GenerateSerializer]
public sealed class FogRootEntry
{
    /// <summary>Gets or sets the entry's name; the root is "/".</summary>
    [Id(0)]
    public string Name { get; set; } = null!;

    /// <summary>Gets or sets the parent path number, or 0 for the root.</summary>
    [Id(1)]
    public ulong Parent { get; set; }

    /// <summary>Gets or sets a value indicating whether the entry is a directory.</summary>
    [Id(2)]
    public bool Directory { get; set; }

    /// <summary>Gets or sets the children by name.</summary>
    [Id(3)]
    public Dictionary<string, ulong> Children { get; set; } = new(StringComparer.Ordinal);
}
