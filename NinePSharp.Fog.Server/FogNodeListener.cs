using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NinePSharp.Core.FSharp;
using NinePSharp.Server;

namespace NinePSharp.Fog.Server;

/// <summary>Explicit direct TLS 1.3 node listener; never enables plaintext or claims AAN support.</summary>
public sealed class FogNodeListener : FogListener
{
    private readonly X509Certificate2 certificate;
    private readonly FogNodePolicy policy;
    private readonly NinePConnectionProcessor processor;
    private readonly TimeSpan handshakeTimeout;

    public FogNodeListener(
        IPEndPoint endpoint,
        X509Certificate2 certificate,
        FogNodePolicy policy,
        INinePFSDispatcher dispatcher,
        ILogger logger,
        int maximumConnections,
        TimeSpan handshakeTimeout,
        TimeSpan sessionLifetime)
        : base(endpoint, logger, Checked(certificate, maximumConnections, handshakeTimeout, sessionLifetime), sessionLifetime)
    {
        this.certificate = certificate;
        this.policy = policy;
        this.handshakeTimeout = handshakeTimeout;
        processor = new NinePConnectionProcessor(logger, dispatcher);
    }

    private protected override async Task ServeAsync(TcpClient client, CancellationToken lifetime)
    {
        await using var tls = new SslStream(client.GetStream(), false, (_, peer, _, _) =>
            peer is X509Certificate2 supplied && policy.AuthenticateCertificate(supplied));
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        handshake.CancelAfter(handshakeTimeout);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13,
            AllowRenegotiation = false,
            AllowTlsResume = false,
        },
            handshake.Token);
        if (tls.RemoteCertificate is not X509Certificate2 peer || !policy.AuthenticateCertificate(peer))
        {
            throw new AuthenticationException();
        }

        var session = new NinePConnectionProcessor.ClientSession();
        session.State = TransportSessionOps.withTransport(session.Dialect, peer, session.State);
        await processor.ProcessStreamAsync(tls, client.Client.RemoteEndPoint, session, lifetime);
    }

    private static int Checked(X509Certificate2 certificate, int maximumConnections, TimeSpan handshakeTimeout, TimeSpan sessionLifetime)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey || maximumConnections <= 0 || handshakeTimeout <= TimeSpan.Zero || sessionLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException("Invalid TLS node listener configuration.");
        }

        return maximumConnections;
    }
}
