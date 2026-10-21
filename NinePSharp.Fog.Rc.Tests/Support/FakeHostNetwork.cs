using System.Net;
using System.Net.Sockets;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Rc.Tests.Support;

// A host whose interfaces and resolver a scenario sets, so what cs makes of them does not depend on
// the machine the tests run on: by default a loopback and an ethernet with an address of each ip
// version, and a resolver that knows only localhost.
internal sealed class FakeHostNetwork : HostNetwork
{
    private static readonly List<Socket> Held = [];

    // Each test process keeps the ports its scenarios name, 17000 to 17999, apart from another's by
    // a slot of its own, held for its life by a socket on the slot's port below the host's ephemeral
    // ones, so runs side by side, as Stryker's are, do not take each other's ports.
    private static readonly int Shift = TakeSlot();
    private int waiting;
    private int resolves;

    public List<HostInterface> Ifcs { get; } =
    [
        new("lo", [new(IPAddress.Loopback, 8), new(IPAddress.IPv6Loopback, 128)]),
        new("ether0", [new(IPAddress.Parse("192.168.1.10"), 24), new(IPAddress.Parse("2001:db8::10"), 64)]),
    ];

    public Dictionary<string, IPAddress[]> Names { get; } = new(StringComparer.Ordinal) { ["localhost"] = [IPAddress.Loopback, IPAddress.IPv6Loopback] };

    public Dictionary<IPAddress, string> Reverse { get; } = new() { [IPAddress.Loopback] = "localhost" };

    // How every lookup fails, when one is set.
    public SocketError? Failure { get; set; }

    // The family Failure fails, or every one.
    public AddressFamily? FailingFamily { get; set; }

    // What every lookup throws, when one is set.
    public Exception? Thrown { get; set; }

    // What every lookup waits for before it answers, when one is set.
    public Task? Gate { get; set; }

    // How many lookups have waited for a gate.
    public int Waiting => waiting;

    // How many times a name's addresses have been looked up.
    public int Resolves => resolves;

    // What every reading of the interfaces waits for, when one is set.
    public ManualResetEventSlim? InterfacesGate { get; set; }

    public override int HostPort(int port) => port is >= 17000 and < 18000 ? port + Shift : port;

    public override int FogPort(int port) => port >= 17000 + Shift && port < 18000 + Shift ? port - Shift : port;

    public override IReadOnlyList<HostInterface> Interfaces()
    {
        InterfacesGate?.Wait();
        return Ifcs;
    }

    public override async Task<IPAddress[]> ResolveAsync(string name, AddressFamily family)
    {
        Interlocked.Increment(ref resolves);
        await AnswerAsync(family);
        return Names.TryGetValue(name, out IPAddress[]? addresses) ? [.. addresses.Where(a => a.AddressFamily == family)] : throw new SocketException((int)SocketError.HostNotFound);
    }

    public override async Task<string> ReverseAsync(IPAddress address)
    {
        await AnswerAsync(address.AddressFamily);
        return Reverse.TryGetValue(address, out string? name) ? name : throw new SocketException((int)SocketError.HostNotFound);
    }

    private static int TakeSlot()
    {
        for (int slot = 0; slot < 14; slot++)
        {
            var hold = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Listening, as SO_REUSEADDR, which .NET sets, lets another bind a port only bound.
                hold.Bind(new IPEndPoint(IPAddress.Loopback, 31900 + slot));
                hold.Listen();
                Held.Add(hold);
                return slot * 1000;
            }
            catch (SocketException)
            {
                hold.Dispose();
            }
        }

        throw new InvalidOperationException("every slot of ports is taken");
    }

    private async Task AnswerAsync(AddressFamily family)
    {
        if (Gate is { } gate)
        {
            Interlocked.Increment(ref waiting);
            await gate;
        }

        if (Thrown is { } thrown)
        {
            throw thrown;
        }

        if (Failure is { } failure && (FailingFamily ?? family) == family)
        {
            throw new SocketException((int)failure);
        }
    }
}
