using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NinePSharp.Fog.Kernel;

// What the host keeps of its network, which Fog, a program on it, takes as the host gives it: the
// addresses of each of its interfaces that is not down, and its resolver.
public class HostNetwork
{
    public static HostNetwork System { get; } = new();

    public virtual IReadOnlyList<HostInterface> Interfaces()
        => [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus != OperationalStatus.Down)
            .Select(ni => new HostInterface(ni.Name, [.. ni.GetIPProperties().UnicastAddresses.Select(a => new HostAddress(a.Address, a.PrefixLength))]))];

    // The addresses of the family the resolver gives the name; a SocketException says why there are none.
    public virtual Task<IPAddress[]> ResolveAsync(string name, AddressFamily family) => Dns.GetHostAddressesAsync(name, family);

    // The name the resolver gives the address.
    public virtual async Task<string> ReverseAsync(IPAddress address) => (await Dns.GetHostEntryAsync(address)).HostName;

    // The host's port for one Fog names, and Fog's for one the host gives: the same, but on a host
    // that keeps one Fog's ports apart from another's.
    public virtual int HostPort(int port) => port;

    public virtual int FogPort(int port) => port;
}
