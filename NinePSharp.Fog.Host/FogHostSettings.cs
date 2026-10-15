using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Host;

public sealed record FogHostSettings(string StorePath, string Owner)
{
    // The size of a store the host reams, in 16 KiB blocks: 1 GiB unless the settings say otherwise.
    public long StoreBlocks { get; init; } = 65536;

    public IReadOnlyDictionary<string, ProgramMain> Programs { get; init; } = new Dictionary<string, ProgramMain>();

    public IReadOnlyList<ExecLoader> Loaders { get; init; } = [];

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
