using System.Net;
using System.Net.Security;
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
        Assert.True(accepting!.IsCompletedSuccessfully);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientsWithoutAnEnrolledCertificateAreRejectedAndReleaseTheirSlot(bool presentCertificate)
    {
        using var fixture = new ControlFixture();
        using var stranger = ControlFixture.Certificate("stranger.test");
        await using var listener = Create(fixture, fixture.ServerCertificate);
        listener.Start();
        var connections = (System.Collections.IDictionary)typeof(FogNodeListener)
            .GetField("connections", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(listener)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(listener.LocalEndpoint, timeout.Token);
            await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            Assert.True(await RejectedAsync(tls, presentCertificate ? stranger : null, timeout.Token));
        }

        while (connections.Count != 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        // One admission slot: a leaked rejected connection would make this enrolled handshake fail.
        await using var enrolled = await FogTlsClient.ConnectAsync(
            listener.LocalEndpoint,
            "control.test",
            FogNodePolicy.SpkiPin(fixture.ServerCertificate),
            fixture.NodeCertificate,
            timeout.Token);
        Assert.Equal(0, fixture.Effects);
        Assert.Empty(fixture.Store.LiveIds());
    }

    [Fact]
    public async Task DisposalBetweenAcceptsEndsTheAcceptLoopWithoutAFault()
    {
        using var fixture = new ControlFixture();
        var listener = Create(fixture, fixture.ServerCertificate);
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var socket = (TcpListener)typeof(FogNodeListener).GetField("listener", Private)!.GetValue(listener)!;
        var stopping = (CancellationTokenSource)typeof(FogNodeListener).GetField("stopping", Private)!.GetValue(listener)!;

        // The order DisposeCoreAsync uses, observed by a loop that is between two accepts.
        socket.Start();
        await stopping.CancelAsync();
        socket.Stop();
        var loop = (Task)typeof(FogNodeListener).GetMethod("AcceptAsync", Private)!.Invoke(listener, null)!;
        await loop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(loop.IsCompletedSuccessfully);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task DisposalWaitsForTheAcceptLoopThenEveryConnectionBeforeReleasingItsToken()
    {
        using var fixture = new ControlFixture();
        var listener = Create(fixture, fixture.ServerCertificate);
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = (CancellationTokenSource)typeof(FogNodeListener).GetField("stopping", Private)!.GetValue(listener)!;
        typeof(FogNodeListener).GetField("accepting", Private)!.SetValue(listener, loop.Task);
        using var accepted = new TcpClient();
        var connections = (System.Collections.Concurrent.ConcurrentDictionary<TcpClient, Task>)typeof(FogNodeListener)
            .GetField("connections", Private)!.GetValue(listener)!;

        Task disposal = listener.DisposeAsync().AsTask();

        // A client accepted while the loop is still running must be awaited too.
        Assert.True(connections.TryAdd(accepted, connection.Task));
        await Task.Delay(50);
        Assert.False(disposal.IsCompleted);
        Assert.True(stopping.IsCancellationRequested);
        _ = stopping.Token;

        loop.SetResult();
        await Task.Delay(50);
        Assert.False(disposal.IsCompleted);
        _ = stopping.Token;

        connection.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Throws<ObjectDisposedException>(() => stopping.Token);
    }

    [Fact]
    public async Task AnAcceptLoopFailureOutsideDisposalIsNotSwallowed()
    {
        using var fixture = new ControlFixture();
        var listener = Create(fixture, fixture.ServerCertificate);
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        // A listener that was never started is not a disposal race; its failure must surface.
        var loop = (Task)typeof(FogNodeListener).GetMethod("AcceptAsync", Private)!.Invoke(listener, null)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.WaitAsync(TimeSpan.FromSeconds(1)));
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task EnrollmentThatLapsesAfterTheHandshakeCallbackIsRecheckedBeforeServing()
    {
        using var fixture = new ControlFixture();

        // Valid for the handshake callback's two clock reads, expired for the post-handshake recheck.
        var clock = new LapsingClock(fixture.NodeCertificate.NotAfter.ToUniversalTime(), validReads: 2);
        var policy = new FogNodePolicy(1, [new("worker", new string('1', 64), FogNodePolicy.SpkiPin(fixture.NodeCertificate), "worker.test")], clock);
        var dispatcher = new FogNinePDispatcher(fixture.Tree, policy, fixture.Limits, fixture.Time);
        await using var listener = new FogNodeListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            fixture.ServerCertificate,
            policy,
            dispatcher,
            NullLogger.Instance,
            1,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(20));
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(listener.LocalEndpoint, timeout.Token);
        await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        Assert.True(await RejectedAsync(tls, fixture.NodeCertificate, timeout.Token));
        Assert.Equal(4, clock.Reads);
        Assert.Empty(fixture.Store.LiveIds());
    }

    private static async Task<bool> RejectedAsync(SslStream tls, X509Certificate2? certificate, CancellationToken cancellation)
    {
        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
            {
                TargetHost = "control.test",
                ClientCertificates = certificate is null ? null : new X509CertificateCollection { certificate },
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
            },
                cancellation);
        }
        catch (Exception exception) when (exception is System.Security.Authentication.AuthenticationException or IOException)
        {
            return true;
        }

        // TLS 1.3 lets the client finish first; the server's rejection then arrives as an alert or a close.
        try
        {
            return await tls.ReadAsync(new byte[1], cancellation) == 0;
        }
        catch (Exception exception) when (exception is System.Security.Authentication.AuthenticationException or IOException)
        {
            return true;
        }
    }

    private static FogNodeListener Create(ControlFixture fixture, X509Certificate2 certificate, TimeSpan? handshake = null, TimeSpan? lifetime = null) =>
        new(
            new IPEndPoint(IPAddress.Loopback, 0),
            certificate,
            fixture.Policy,
            fixture.Dispatcher,
            NullLogger.Instance,
            1,
            handshake ?? TimeSpan.FromSeconds(5),
            lifetime ?? TimeSpan.FromSeconds(20));

    private sealed class LapsingClock(DateTime expired, int validReads) : TimeProvider
    {
        private int reads;

        internal int Reads => Volatile.Read(ref reads);

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref reads) <= validReads ? DateTimeOffset.UtcNow : new DateTimeOffset(expired, TimeSpan.Zero);
    }
}
