using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devip: #I, tcp's conversations as devip and tcp.c serve them, over the host's sockets as
// drawterm's devip does. The host routes and keeps the addresses, so a call or an announce the host
// refuses fails with its reason.
internal sealed class IpDevice(FogKernel kernel) : IResourceDataOperations
{
    public const string Provider = "ip";
    private const string Network = "network";
    private const uint Directory = (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101;
    private const int MaxConversations = 0xFFF;
    private static readonly string[] Protocols = ["tcp"];
    private readonly List<Conversation> tcp = [];

    private enum State
    {
        Idle,
        Announced,
        Connecting,
        Connected,
    }

    private enum File
    {
        Topdir = 1,
        Protodir = 3,
        Clone = 4,
        Convdir = 5,
        Ctl = 6,
        Data = 7,
        Listen = 8,
        Local = 9,
        Remote = 10,
        Status = 11,
    }

    public static ResourceHandle Root { get; } = Handle(0, 0, File.Topdir);

    private static (string Name, File File)[] ConvFiles { get; } =
    [
        ("ctl", File.Ctl), ("data", File.Data), ("listen", File.Listen), ("local", File.Local), ("remote", File.Remote), ("status", File.Status),
    ];

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        ResourceHandle? found = null;
        switch (Type(directory))
        {
            case File.Topdir when Array.IndexOf(Protocols, name) is int p and >= 0:
                found = Handle(p, 0, File.Protodir);
                break;
            case File.Protodir when name == "clone":
                found = Handle(0, 0, File.Clone);
                break;
            case File.Protodir when int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int x) && Number(x) == name && x < Count():
                found = Handle(0, x, File.Convdir);
                break;
            case File.Convdir when ConvFiles.FirstOrDefault(f => f.Name == name) is { Name: not null } file:
                found = Handle(0, Conv(directory), file.File);
                break;
        }

        return ValueTask.FromResult(found);
    }

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
    {
        IReadOnlyList<ResourceDirectoryEntry> entries = Type(directory) switch
        {
            File.Topdir => [.. Protocols.Select((name, p) => new ResourceDirectoryEntry(name, Handle(p, 0, File.Protodir)))],
            File.Protodir => [.. Conversations().Select(c => new ResourceDirectoryEntry(Number(c.X), Handle(0, c.X, File.Convdir))), new ResourceDirectoryEntry("clone", Handle(0, 0, File.Clone))],
            File.Convdir => [.. ConvFiles.Select(f => new ResourceDirectoryEntry(f.Name, Handle(0, Conv(directory), f.File)))],
            _ => [],
        };
        return ValueTask.FromResult(entries);
    }

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        File type = Type(resource);
        if (type is File.Topdir or File.Protodir or File.Clone)
        {
            // Stryker disable once String : the root and clone stat under the names they were reached by, as devstat names a file from its channel
            return ValueTask.FromResult(type switch
            {
                File.Topdir => new ResourceStat(resource, "#I0", Directory, 0, 0, 0, Network, Network, Network),
                File.Protodir => new ResourceStat(resource, Protocols[Proto(resource)], Directory, 0, 0, 0, Network, Network, Network),
                _ => new ResourceStat(resource, "clone", 0b110_110_110, 0, 0, 0, Network, Network, Network),
            });
        }

        Conversation c = Get(Conv(resource));
        lock (c)
        {
            (string name, uint mode) = type switch
            {
                File.Convdir => (Number(c.X), Directory),
                File.Ctl or File.Data or File.Listen => (Name(type), c.Perm),
                _ => (Name(type), 0b100_100_100u),
            };
            return ValueTask.FromResult(new ResourceStat(resource, name, mode, 0, c.Ctime, 0, c.Owner, c.Owner, c.Owner));
        }
    }

    // ipopen: clone reserves a conversation and opens its ctl; listen waits for a call and opens its
    // ctl; ctl, data and listen open by the owner's bits for the owner, by the others' for anyone
    // else, and always for the host owner. devip compares the owner with #I's attacher; Fog, whose
    // channels do not carry one, with the process's user.
    public async ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        int perm = (mode & 3) switch
        {
            NinePConstants.OREAD => 4,
            NinePConstants.OWRITE => 2,
            NinePConstants.ORDWR => 6,
            _ => 0,
        };
        string handle = $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";
        switch (Type(resource))
        {
            case File.Topdir or File.Protodir or File.Convdir or File.Status or File.Remote or File.Local:
                if ((mode & 3) != NinePConstants.OREAD)
                {
                    throw new IOException(Errors.Permission);
                }

                return new ResourceOpenHandle(resource, handle, mode, 0);
            case File.Clone:
                Conversation cloned = Clone(context.User, null);
                return new ResourceOpenHandle(Handle(0, cloned.X, File.Ctl), handle, mode, 0);
            case File.Ctl or File.Data:
                Conversation c = Get(Conv(resource));
                lock (c)
                {
                    Check(c, perm, context);
                    if (++c.Ref == 1)
                    {
                        c.Owner = context.User;
                        c.Perm = 0b110_110_000;
                    }
                }

                return new ResourceOpenHandle(resource, handle, mode, 0);
            default:
                return await ListenAsync(Get(Conv(resource)), perm, handle, mode, context, cancellationToken);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        File type = Type(openHandle.Resource);
        Conversation c = Get(Conv(openHandle.Resource));
        if (type == File.Data)
        {
            Socket? socket;
            lock (c)
            {
                socket = c.Socket;
            }

            if (socket is null)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            byte[] buffer = new byte[count];
            int n = await Host(() => socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).AsTask());
            return buffer.AsMemory(0, n);
        }

        string text;
        lock (c)
        {
            text = type switch
            {
                File.Ctl => Number(c.X),
                File.Local => $"{Format(c.LocalAddress)}!{c.LocalPort}\n",
                File.Remote => $"{Format(c.RemoteAddress)}!{c.RemotePort}\n",
                File.Status => $"{c.TcpState} qin {(c.State == State.Announced ? 0 : c.Socket?.Available ?? 0)} qout 0\n",
                _ => throw new IOException(Errors.Permission),
            };
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return bytes.AsMemory((int)Math.Min(offset, (ulong)bytes.Length));
    }

    public async ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        Conversation c = Get(Conv(openHandle.Resource));
        switch (Type(openHandle.Resource))
        {
            case File.Ctl:
                await ControlAsync(c, Encoding.UTF8.GetString(data.Span), context, cancellationToken);
                return (uint)data.Length;
            case File.Data:
                Socket socket;
                lock (c)
                {
                    socket = c.Socket ?? throw new IOException(PipeQueue.Hungup);
                }

                int sent = 0;
                while (sent < data.Length)
                {
                    sent += await Host(() => socket.SendAsync(data[sent..], SocketFlags.None, cancellationToken).AsTask());
                }

                return (uint)data.Length;
            default:
                throw new IOException(Errors.Permission);
        }
    }

    // ipclose: when the last of a conversation's ctl and data closes, it goes back to the network.
    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (Type(openHandle.Resource) is File.Ctl or File.Data)
        {
            Conversation c = Get(Conv(openHandle.Resource));
            lock (c)
            {
                if (--c.Ref == 0)
                {
                    c.Owner = Network;
                    c.Perm = 0b110_110_110;
                    c.Close();
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    // The host's reason a socket call failed, in Plan 9's words where they have them.
    internal static IOException Reason(SocketException error) => new(error.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "connection refused",
        SocketError.TimedOut => "connection timed out",
        SocketError.AddressAlreadyInUse => "address in use",
        SocketError.AddressNotAvailable => "not a local IP address",
        SocketError.AccessDenied => Errors.Permission,
        SocketError.ConnectionReset => "connection reset by peer",
        _ => error.Message,
    });

    private static ResourceHandle Handle(int proto, int conv, File type)
    {
        // Stryker disable once Bitwise : tcp is #I's only protocol, numbered 0, which shifts to 0 either way
        int protocol = proto << 16;
        return new(new ResourceIdentity(Provider, "#I", (ulong)(protocol | (conv << 4) | (int)type)), type is File.Topdir or File.Protodir or File.Convdir ? QidType.QTDIR : QidType.QTFILE);
    }

    private static File Type(ResourceHandle resource) => (File)(int)(resource.Identity.Path & 0xF);

    private static int Conv(ResourceHandle resource)
    {
        // Stryker disable once Bitwise : a path is unsigned, so >> already shifts in zeros as >>> does
        ulong bits = resource.Identity.Path >> 4;
        return (int)(bits & 0xFFF);
    }

    private static int Proto(ResourceHandle resource)
    {
        // Stryker disable once Bitwise : tcp is #I's only protocol, numbered 0, and any shift of a path's protocol bits gives 0
        ulong bits = resource.Identity.Path >> 16;
        return (int)(bits & 0xFF);
    }

    // A conversation file's name; ConvFiles holds them in the order of their types.
    private static string Name(File type) => ConvFiles[type - File.Ctl].Name;

    private static string Number(int x) => x.ToString(CultureInfo.InvariantCulture);

    // %I: an IPv4 address as v4, any other as v6, and none as ::.
    private static string Format(IPAddress? address)
        => address is null ? "::" : address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

    // A host socket call, failing with the reason Reason gives, or as hung up once its socket is let go.
    private static async Task<T> Host<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            throw error is SocketException socket ? Reason(socket) : new IOException(PipeQueue.Hungup);
        }
    }

    // parseip: a v4 or v6 address, or the error devip gives.
    private static IPAddress ParseIp(string text)
        => IPAddress.TryParse(text, out IPAddress? address) ? address : throw new IOException("bad ip address syntax");

    // strtol over the port, which must end the field or come before a !.
    private static int ParsePort(string text)
    {
        int end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        if (end == 0 || (end < text.Length && text[end] != '!'))
        {
            throw new IOException("bad numeric port");
        }

        return int.Parse(text[..end], CultureInfo.InvariantCulture);
    }

    private IEnumerable<Conversation> Conversations()
    {
        lock (tcp)
        {
            return [.. tcp];
        }
    }

    private int Count()
    {
        lock (tcp)
        {
            return tcp.Count;
        }
    }

    // A conversation a walk found; once made, one is never taken away.
    private Conversation Get(int x)
    {
        lock (tcp)
        {
            return tcp[x];
        }
    }

    // The permission check ipopen makes of ctl, data and listen. #I has no wstat, so a conversation's
    // owner bits are always rw and only the others' bits can refuse.
    private void Check(Conversation c, int perm, ResourceOperationContext context)
    {
        if (context.User != c.Owner && (perm & c.Perm) != perm && context.User != kernel.User)
        {
            throw new IOException(Errors.Permission);
        }
    }

    // protoclone: the lowest conversation no one holds, or a new one.
    private Conversation Clone(string user, Socket? socket)
    {
        lock (tcp)
        {
            Conversation? c = null;
            foreach (Conversation candidate in tcp)
            {
                lock (candidate)
                {
                    if (candidate.Ref == 0)
                    {
                        c = candidate;
                        break;
                    }
                }
            }

            if (c is null)
            {
                if (tcp.Count >= MaxConversations)
                {
                    throw new IOException("no free devices");
                }

                c = new Conversation(tcp.Count);
                tcp.Add(c);
            }

            lock (c)
            {
                c.Reset(user, socket, (uint)kernel.Clock.GetUtcNow().ToUnixTimeSeconds());
            }

            return c;
        }
    }

    private async Task<ResourceOpenHandle> ListenAsync(Conversation listening, int perm, string handle, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        Socket socket;
        lock (listening)
        {
            Check(listening, perm, context);
            if (listening.State != State.Announced)
            {
                throw new IOException("not announced");
            }

            socket = listening.Socket!;
        }

        Socket accepted;
        try
        {
            accepted = await Host(() => socket.AcceptAsync(cancellationToken).AsTask());
        }
        catch (IOException) when (listening.State != State.Announced)
        {
            // ipopen gives up on a conversation hung up while it waits.
            throw new IOException("listen hungup");
        }

        Conversation call = Clone(context.User, accepted);
        lock (call)
        {
            call.State = State.Connected;
            call.SetEnds(kernel.Host);
        }

        return new ResourceOpenHandle(Handle(0, call.X, File.Ctl), handle, mode, 0);
    }

    // ipwrite of ctl: devip's connect, announce and bind, and tcp.c's hangup, close and keepalive.
    private async Task ControlAsync(Conversation c, string message, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        List<string> f = Process.Tokenize(message, 10);
        if (f.Count < 1)
        {
            throw new IOException("short control request");
        }

        switch (f[0])
        {
            case "connect":
                await ConnectAsync(c, f, cancellationToken);
                break;
            case "announce":
                Announce(c, f, context);
                break;
            case "bind":
                if (f.Count != 2)
                {
                    throw new IOException("bad args to bind");
                }

                lock (c)
                {
                    c.SetLocal(f[1]);
                }

                break;
            case "hangup" or "close" when f.Count == 1:
                lock (c)
                {
                    c.Close();
                }

                break;
            case "keepalive":
                lock (c)
                {
                    // Stryker disable once Statement,Boolean : keepalive is the host socket's, which no file of #I shows
                    c.Socket?.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                }

                break;
            default:
                throw new IOException("unknown control request");
        }
    }

    private async Task ConnectAsync(Conversation c, List<string> f, CancellationToken cancellationToken)
    {
        Socket socket;
        IPEndPoint remote;
        lock (c)
        {
            if (c.State != State.Idle)
            {
                throw new IOException("connection in use");
            }

            if (f.Count is not (2 or 3))
            {
                throw new IOException("bad args to connect");
            }

            int bang = f[1].IndexOf('!', StringComparison.Ordinal);
            if (bang < 0)
            {
                throw new IOException("malformed address");
            }

            IPAddress address = ParseIp(f[1][..bang]);
            int port = ParsePort(f[1][(bang + 1)..]);

            // Fsstdconnect: a local address given, or the host's choice, whatever was bound before.
            if (f.Count == 3)
            {
                c.SetLocal(f[2]);
            }
            else
            {
                (c.LocalAddress, c.LocalPort) = (null, 0);
            }

            remote = new IPEndPoint(address, kernel.Host.HostPort(port));
            socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (c.LocalPort != 0 || c.LocalAddress is not null)
            {
                socket.Bind(new IPEndPoint(c.LocalAddress ?? (address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any), kernel.Host.HostPort(c.LocalPort)));
            }

            c.State = State.Connecting;
            c.Socket = socket;
        }

        try
        {
            await Host(async () =>
            {
                await socket.ConnectAsync(remote, cancellationToken);
                return 0;
            });
        }
        catch
        {
            lock (c)
            {
                c.Close();
            }

            throw;
        }

        lock (c)
        {
            c.State = State.Connected;
            c.SetEnds(kernel.Host);
        }
    }

    // Fsstdannounce and setladdrport: [addr!]port, * for any address, and * as the port for every
    // port, which only the host owner may take and the host's sockets cannot give.
    private void Announce(Conversation c, List<string> f, ResourceOperationContext context)
    {
        lock (c)
        {
            if (c.State != State.Idle)
            {
                throw new IOException("connection in use");
            }

            if (f.Count != 2)
            {
                throw new IOException("bad args to announce");
            }

            // What follows the !, or all of it when there is none.
            string port = f[1][(f[1].IndexOf('!', StringComparison.Ordinal) + 1)..];
            if (port == "*")
            {
                throw new IOException(context.User == kernel.User ? "announce of every port is not supported" : Errors.Permission);
            }

            c.SetLocal(f[1]);
            IPAddress address = c.LocalAddress ?? IPAddress.IPv6Any;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (address.Equals(IPAddress.IPv6Any))
                {
                    socket.DualMode = true;
                }

                socket.Bind(new IPEndPoint(address, kernel.Host.HostPort(c.LocalPort)));
                socket.Listen();
            }
            catch (SocketException error)
            {
                // Stryker disable once Statement : a socket that could not bind holds no port; disposing it only gives its handle back sooner
                socket.Dispose();
                throw Reason(error);
            }

            c.Socket = socket;
            c.State = State.Announced;
            c.LocalPort = kernel.Host.FogPort(((IPEndPoint)socket.LocalEndPoint!).Port);
        }
    }

    private sealed class Conversation(int x)
    {
        public int X { get; } = x;

        public int Ref { get; set; }

        public string Owner { get; set; } = Network;

        public uint Perm { get; set; } = 0b110_110_110;

        public State State { get; set; }

        public Socket? Socket { get; set; }

        public IPAddress? LocalAddress { get; set; }

        public int LocalPort { get; set; }

        public IPAddress? RemoteAddress { get; set; }

        public int RemotePort { get; set; }

        public uint Ctime { get; private set; }

        // tcp.c's state names, for status.
        public string TcpState => State switch
        {
            State.Announced => "Listen",
            State.Connecting => "Syn_sent",
            State.Connected => "Established",
            _ => "Closed",
        };

        public void Reset(string user, Socket? socket, uint now)
        {
            Ref = 1;
            Owner = user;
            Perm = 0b110_110_000;
            State = State.Idle;
            Socket = socket;
            LocalAddress = RemoteAddress = null;
            LocalPort = RemotePort = 0;
            Ctime = now;
        }

        // setladdrport: [addr!]port; without an address, or with *, any when announcing and the
        // host's choice otherwise, as setladdr gives the source address of the remote.
        public void SetLocal(string text)
        {
            string port = text;
            LocalAddress = null;
            int bang = text.IndexOf('!', StringComparison.Ordinal);
            if (bang >= 0)
            {
                string address = text[..bang];
                port = text[(bang + 1)..];
                LocalAddress = address == "*" ? null : ParseIp(address);
            }

            LocalPort = ParsePort(port);
        }

        public void SetEnds(HostNetwork host)
        {
            if (Socket?.LocalEndPoint is IPEndPoint local)
            {
                (LocalAddress, LocalPort) = (local.Address, host.FogPort(local.Port));
            }

            if (Socket?.RemoteEndPoint is IPEndPoint remote)
            {
                (RemoteAddress, RemotePort) = (remote.Address, host.FogPort(remote.Port));
            }
        }

        // tcphangup and closeconv: the socket is shut down and let go, and the conversation is idle.
        public void Close()
        {
            if (Socket is { } socket)
            {
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException)
                {
                    // A socket never connected has nothing to shut down.
                }

                // Stryker disable once Statement : Shutdown has already ended the socket's calls, and a listener's listening; Dispose gives the host its handle back
                socket.Dispose();
            }

            Socket = null;
            State = State.Idle;
        }
    }
}
