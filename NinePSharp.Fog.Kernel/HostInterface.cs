namespace NinePSharp.Fog.Kernel;

// One of the host's interfaces, and its addresses.
public sealed record HostInterface(string Name, IReadOnlyList<HostAddress> Addresses);
