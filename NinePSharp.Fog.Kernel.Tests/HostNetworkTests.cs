using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class HostNetworkTests
{
    [Fact]
    public void TheHostsInterfacesAreThoseNotDownWithTheirAddresses()
    {
        IReadOnlyList<HostInterface> interfaces = HostNetwork.System.Interfaces();
        string[] up = [.. NetworkInterface.GetAllNetworkInterfaces().Where(ni => ni.OperationalStatus != OperationalStatus.Down).Select(ni => ni.Name)];
        Assert.Equal(up, interfaces.Select(i => i.Name));
        Assert.Contains(interfaces.SelectMany(i => i.Addresses), a => a.Address.Equals(IPAddress.Loopback) && a.PrefixLength == 8);
    }

    [Fact]
    public async Task TheHostsResolverGivesTheAddressesOfAFamilyAndTheNameOfAnAddress()
    {
        Assert.Contains(IPAddress.Loopback, await HostNetwork.System.ResolveAsync("localhost", AddressFamily.InterNetwork));
        Assert.DoesNotContain(await HostNetwork.System.ResolveAsync("localhost", AddressFamily.InterNetwork), a => a.AddressFamily != AddressFamily.InterNetwork);
        Assert.Equal((await Dns.GetHostEntryAsync(IPAddress.Loopback)).HostName, await HostNetwork.System.ReverseAsync(IPAddress.Loopback));
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, "connection refused")]
    [InlineData(SocketError.TimedOut, "connection timed out")]
    [InlineData(SocketError.AddressAlreadyInUse, "address in use")]
    [InlineData(SocketError.AddressNotAvailable, "not a local IP address")]
    [InlineData(SocketError.AccessDenied, "permission denied")]
    [InlineData(SocketError.ConnectionReset, "connection reset by peer")]
    public void TheHostsSocketErrorsAreGivenInPlan9sWords(SocketError error, string message)
        => Assert.Equal(message, IpDevice.Reason(new SocketException((int)error)).Message);

    [Fact]
    public void ASocketErrorPlan9HasNoWordsForIsGivenInTheHostsWords()
    {
        var error = new SocketException((int)SocketError.NetworkUnreachable);
        Assert.Equal(error.Message, IpDevice.Reason(error).Message);
    }

    [Fact]
    public async Task AKernelAndItsProcessesAskTheHostTheyAreGiven()
    {
        var host = new HostNetwork();
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), host: host).BootAsync();
        Assert.Same(host, init.Host);
        Assert.Same(HostNetwork.System, (await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync()).Host);
    }
}
