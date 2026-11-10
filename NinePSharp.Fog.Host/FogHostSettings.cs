using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Host;

public sealed record FogHostSettings(string StorePath, string Owner)
{
    // The size of a store the host reams, in 16 KiB blocks: 1 GiB unless the settings say otherwise.
    public long StoreBlocks { get; init; } = 65536;

    public IReadOnlyDictionary<string, ProgramMain> Programs { get; init; } = new Dictionary<string, ProgramMain>();

    public IReadOnlyList<ExecLoader> Loaders { get; init; } = [];

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    // The host's sockets, its console and its nvram, as the kernel is given them.
    public HostNetwork Host { get; init; } = HostNetwork.System;

    public HostConsole Console { get; init; } = HostConsole.System;

    public HostNvram Nvram { get; init; } = HostNvram.None;

    // The files 9front's installer would copy, each named from the store's root, written again at
    // each boot as the programs are.
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    // What init runs once the namespace is built, as bootrc goes on to cpurc; none if empty.
    public IReadOnlyList<string> Boot { get; init; } = [];

    // Where the store writes what gefs writes on its standard error.
    public TextWriter Log { get; init; } = TextWriter.Null;
}
