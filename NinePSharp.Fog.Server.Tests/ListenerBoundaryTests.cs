using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Fog.Server;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class ListenerBoundaryTests
{
    [Fact]
    public void ListenerNeedsAPrivateKeyAndNonNullCertificate()
    {
        using var fixture = new ControlFixture();
        using var publicOnly = X509CertificateLoader.LoadCertificate(fixture.ServerCertificate.Export(X509ContentType.Cert));
        Assert.Throws<ArgumentNullException>(() => Create(fixture, null!));
        Assert.Equal("Invalid TLS node listener configuration.", Assert.Throws<ArgumentException>(() => Create(fixture, publicOnly)).Message);
    }

    [Fact]
    public async Task AdmissionLimitRejectsAnExtraConnectionAndDisposalClosesThePort()
    {
        using var fixture = new ControlFixture();
        await using var listener = Create(fixture, fixture.ServerCertificate);
        listener.Start();
        var endpoint = listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var first = await FogTlsClient.ConnectAsync(endpoint, "control.test", FogNodePolicy.SpkiPin(fixture.ServerCertificate), fixture.NodeCertificate, timeout.Token);
        using var extra = new TcpClient();
        await extra.ConnectAsync(endpoint, timeout.Token);
        Assert.Equal(0, await extra.GetStream().ReadAsync(new byte[1], timeout.Token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250)));
        Task? accepting = (Task?)typeof(FogNodeListener)
            .GetField("accepting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(listener);
        var connections = (System.Collections.IDictionary)typeof(FogNodeListener)
            .GetField("connections", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(listener)!;
        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
        Assert.True(accepting!.IsCompleted);
        Assert.Empty(connections);
        Assert.Equal(0, await first.ReadAsync(new byte[1], timeout.Token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250)));
        using var closed = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => closed.ConnectAsync(endpoint, timeout.Token).AsTask());
    }

    [Fact]
    public async Task HandshakeDeadlineClosesAnIdleUnauthenticatedSocket()
    {
        using var fixture = new ControlFixture();
        await using var listener = Create(fixture, fixture.ServerCertificate, handshake: TimeSpan.FromMilliseconds(50));
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var idle = new TcpClient();
        await idle.ConnectAsync(listener.LocalEndpoint, timeout.Token);
        Assert.Equal(0, await idle.GetStream().ReadAsync(new byte[1], timeout.Token));
        Assert.Empty(fixture.Store.LiveIds());
    }

    [Fact]
    public async Task SessionDeadlineClosesAnAuthenticatedIdleSocket()
    {
        using var fixture = new ControlFixture();
        await using var listener = Create(fixture, fixture.ServerCertificate, lifetime: TimeSpan.FromSeconds(1));
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var tls = await FogTlsClient.ConnectAsync(listener.LocalEndpoint, "control.test", FogNodePolicy.SpkiPin(fixture.ServerCertificate), fixture.NodeCertificate, timeout.Token);
        Assert.Equal(0, await tls.ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(0, fixture.Effects);
    }

    [Fact]
    public async Task DisposalBeforeStartIsSafeAndPreventsStarting()
    {
        using var fixture = new ControlFixture();
        var listener = Create(fixture, fixture.ServerCertificate);
        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
        Assert.Throws<ObjectDisposedException>(listener.Start);
    }

    [Fact]
    public async Task StartIsSingleUseAndAcceptedSocketsDisableNagle()
    {
        using var fixture = new ControlFixture();
        await using var listener = Create(fixture, fixture.ServerCertificate);
        listener.Start();
        Assert.Equal("Listener already started.", Assert.Throws<InvalidOperationException>(listener.Start).Message);

        using var client = new TcpClient();
        Assert.Same(client, FogNodeListener.ConfigureAcceptedClient(client));
        Assert.True(client.NoDelay);
    }

    [Fact]
    public async Task DisposalDisposesItsCancellationSource()
    {
        using var fixture = new ControlFixture();
        var listener = Create(fixture, fixture.ServerCertificate);
        var source = (CancellationTokenSource)typeof(FogNodeListener)
            .GetField("stopping", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(listener)!;

        await listener.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => source.Cancel());
    }

    private static FogNodeListener Create(ControlFixture fixture, X509Certificate2 certificate, TimeSpan? handshake = null, TimeSpan? lifetime = null) =>
        new(new IPEndPoint(IPAddress.Loopback, 0), certificate, fixture.Policy, fixture.Dispatcher, NullLogger.Instance,
            1, handshake ?? TimeSpan.FromSeconds(5), lifetime ?? TimeSpan.FromSeconds(20));
}
