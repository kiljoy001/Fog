using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NinePSharp.Server;

namespace NinePSharp.Fog.Server;

/// <summary>
/// Plain 9P for users, as a 9front file server listens on tcp!*!564: clients authenticate on the
/// afid, and nothing on this listener carries a certificate. dp9ik authenticates both ends but does
/// not encrypt the connection.
/// </summary>
public sealed class FogUserListener : FogListener
{
    private readonly NinePConnectionProcessor processor;

    public FogUserListener(IPEndPoint endpoint, INinePFSDispatcher dispatcher, ILogger logger, int maximumConnections, TimeSpan sessionLifetime, TimeSpan? drain = null)
        : base(endpoint, logger, Checked(maximumConnections, sessionLifetime, drain), sessionLifetime, drain)
    {
        processor = new NinePConnectionProcessor(logger, dispatcher) { Drain = Drain };
    }

    private protected override Task ServeAsync(TcpClient client, CancellationToken lifetime) =>
        processor.ProcessStreamAsync(client.GetStream(), client.Client.RemoteEndPoint, new NinePConnectionProcessor.ClientSession(), lifetime);

    private static int Checked(int maximumConnections, TimeSpan sessionLifetime, TimeSpan? drain) =>
        maximumConnections <= 0 || sessionLifetime <= TimeSpan.Zero || drain <= TimeSpan.Zero
            ? throw new ArgumentException("Invalid user listener configuration.")
            : maximumConnections;
}
