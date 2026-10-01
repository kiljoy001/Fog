using System.Collections.Concurrent;
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
public sealed class FogNodeListener : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly FogNodePolicy policy;
    private readonly NinePConnectionProcessor processor;
    private readonly int maximumConnections;
    private readonly TimeSpan handshakeTimeout;
    private readonly TimeSpan sessionLifetime;
    private readonly ConcurrentDictionary<TcpClient, Task> connections = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly object lifecycleGate = new();
    private Task? accepting;
    private Task? disposal;

    public FogNodeListener(IPEndPoint endpoint, X509Certificate2 certificate, FogNodePolicy policy,
        FogNinePDispatcher dispatcher, ILogger logger, int maximumConnections, TimeSpan handshakeTimeout, TimeSpan sessionLifetime)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey || maximumConnections <= 0 || handshakeTimeout <= TimeSpan.Zero || sessionLifetime <= TimeSpan.Zero)
            throw new ArgumentException("Invalid TLS node listener configuration.");
        this.certificate = certificate;
        this.policy = policy;
        this.maximumConnections = maximumConnections;
        this.handshakeTimeout = handshakeTimeout;
        this.sessionLifetime = sessionLifetime;
        listener = new TcpListener(endpoint);
        processor = new NinePConnectionProcessor(logger, dispatcher);
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)listener.LocalEndpoint;

    public void Start()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (accepting is not null) throw new InvalidOperationException("Listener already started.");
            listener.Start();
            accepting = AcceptAsync();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = ConfigureAcceptedClient(await listener.AcceptTcpClientAsync(stopping.Token));
                if (connections.Count >= maximumConnections)
                {
                    client.Dispose();
                }
                else
                {
                    var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    connections.TryAdd(client, finished.Task);
                    _ = ServeAsync(client, finished);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, TaskCompletionSource finished)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            lifetime.CancelAfter(sessionLifetime);
            await using var tls = new SslStream(client.GetStream(), false, (_, peer, _, _) =>
                peer is X509Certificate2 supplied && policy.AuthenticateCertificate(supplied));
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            handshake.CancelAfter(handshakeTimeout);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls13,
                AllowRenegotiation = false,
                AllowTlsResume = false,
            }, handshake.Token);
            if (tls.RemoteCertificate is not X509Certificate2 peer || !policy.AuthenticateCertificate(peer)) throw new AuthenticationException();
            var session = new NinePConnectionProcessor.ClientSession();
            session.State = TransportSessionOps.withTransport(session.Dialect, peer, session.State);
            await processor.ProcessStreamAsync(tls, client.Client.RemoteEndPoint, session, lifetime.Token);
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // No unauthenticated request, certificate data, proof or private exception is sent on the wire.
        }
        finally
        {
            client.Dispose();
            connections.TryRemove(client, out _);
            finished.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate) return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var active = connections.ToArray();
        await stopping.CancelAsync();
        listener.Stop();
        await Task.WhenAll(active.Select(connection => connection.Value));
        stopping.Dispose();
    }

    internal static TcpClient ConfigureAcceptedClient(TcpClient client)
    {
        client.NoDelay = true;
        return client;
    }
}
