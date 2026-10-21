using System.Net;

namespace NinePSharp.Fog.Kernel;

// An address of a host interface, with the length of its network's prefix.
public sealed record HostAddress(IPAddress Address, int PrefixLength);
