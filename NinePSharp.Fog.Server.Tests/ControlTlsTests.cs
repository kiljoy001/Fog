using System.Security.Authentication;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class ControlTlsTests
{
    [Fact]
    public void ClientSocketCertificateBoundariesAndTlsOptionsAreExplicit()
    {
        using var fixture = new ControlFixture();
        using TcpClient connection = FogTlsClient.CreateConnection(AddressFamily.InterNetwork);
        Assert.True(connection.NoDelay);

        DateTime first = fixture.ServerCertificate.NotBefore.ToUniversalTime();
        DateTime last = fixture.ServerCertificate.NotAfter.ToUniversalTime();
        string pin = FogNodePolicy.SpkiPin(fixture.ServerCertificate);
        Assert.True(FogTlsClient.ValidateServerCertificate(fixture.ServerCertificate, "control.test", pin, first));
        Assert.False(FogTlsClient.ValidateServerCertificate(fixture.ServerCertificate, "control.test", pin, first.AddTicks(-1)));
        Assert.True(FogTlsClient.ValidateServerCertificate(fixture.ServerCertificate, "control.test", pin, last.AddTicks(-1)));
        Assert.False(FogTlsClient.ValidateServerCertificate(fixture.ServerCertificate, "control.test", pin, last));
        Assert.False(FogTlsClient.ValidateServerCertificate(fixture.ServerCertificate, "wrong.test", pin, first));
        Assert.False(FogTlsClient.ValidateServerCertificate(fixture.ServerCertificate, "control.test", new string('0', 64), first));

        SslClientAuthenticationOptions options = FogTlsClient.CreateAuthenticationOptions("control.test", fixture.NodeCertificate);
        Assert.Equal("control.test", options.TargetHost);
        Assert.Same(fixture.NodeCertificate, Assert.Single(options.ClientCertificates!.Cast<X509Certificate2>()));
        Assert.Equal(SslProtocols.Tls13, options.EnabledSslProtocols);
        Assert.False(options.AllowRenegotiation);
        Assert.False(options.AllowTlsResume);
    }

    [Fact]
    public async Task InvalidPinnedConfigurationFailsBeforeConnecting()
    {
        using var fixture = new ControlFixture();
        using var publicOnly = X509CertificateLoader.LoadCertificate(fixture.NodeCertificate.Export(X509ContentType.Cert));
        var endpoint = new IPEndPoint(IPAddress.Loopback, 1);
        string pin = FogNodePolicy.SpkiPin(fixture.ServerCertificate);
        foreach (var invalid in new[] { ("", pin, fixture.NodeCertificate), (" ", pin, fixture.NodeCertificate),
            ("control.test", "a", fixture.NodeCertificate), ("control.test", "g" + pin[1..], fixture.NodeCertificate),
            ("control.test", pin, publicOnly) })
            Assert.Equal("Invalid pinned TLS client configuration.", (await Assert.ThrowsAsync<ArgumentException>(() =>
                FogTlsClient.ConnectAsync(endpoint, invalid.Item1, invalid.Item2, invalid.Item3, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task FailedConnectionDisposesTheCreatedSocket()
    {
        using var fixture = new ControlFixture();
        TcpClient? created = null;

        await Assert.ThrowsAnyAsync<SocketException>(() => FogTlsClient.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, 1), "control.test", FogNodePolicy.SpkiPin(fixture.ServerCertificate),
            fixture.NodeCertificate, CancellationToken.None, family => created = FogTlsClient.CreateConnection(family)));

        Assert.NotNull(created);
        Assert.Null(created.Client);
    }

    [Theory]
    [InlineData("wildcard")]
    [InlineData("common-name")]
    [InlineData("expired")]
    [InlineData("future")]
    public async Task MatchingPinDoesNotOverrideTheExactSanAndValidityRequirements(string kind)
    {
        using var fixture = new ControlFixture();
        using var certificate = ControlFixture.Certificate(kind == "wildcard" ? "*.example.test" : "control.test",
            includeSan: kind != "common-name",
            notBefore: DateTimeOffset.UtcNow.AddHours(kind == "future" ? 1 : -2),
            notAfter: DateTimeOffset.UtcNow.AddHours(kind == "expired" ? -1 : 2));
        await using var listener = new FogNodeListener(new IPEndPoint(IPAddress.Loopback, 0), certificate, fixture.Policy,
            fixture.Dispatcher, NullLogger.Instance, 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AuthenticationException>(() => FogTlsClient.ConnectAsync(listener.LocalEndpoint,
            kind == "wildcard" ? "control.example.test" : "control.test", FogNodePolicy.SpkiPin(certificate), fixture.NodeCertificate, timeout.Token));
        Assert.Empty(fixture.Store.LiveIds());
    }

    [Fact]
    public async Task BoundedClientFragmentsAndReleasesCompletedTransactions()
    {
        using var fixture = new ControlFixture();
        await using var listener = fixture.Listen();
        var client = new FogTransactionClient(async cancellation => await FogTlsClient.ConnectAsync(listener.LocalEndpoint,
            "control.test", FogNodePolicy.SpkiPin(fixture.ServerCertificate), fixture.NodeCertificate, cancellation),
            "worker", 256, 4096, TimeSpan.FromSeconds(1));
        byte[] input = Enumerable.Range(0, 3000).Select(n => (byte)n).ToArray();
        for (int iteration = 0; iteration < 5; iteration++)
        {
            var result = await client.ExecuteAsync("fixture", new Dictionary<string, byte[]> { ["request"] = input }, ["reply", "extra"]);
            Assert.Equal(input, result["reply"]);
            Assert.Equal(new byte[] { 9 }, result["extra"]);
            Assert.Empty(fixture.Store.LiveIds());
        }
        Assert.Equal(5, fixture.Effects);
    }

    [Fact]
    public async Task RealTls13AndExisting9PClientCanCommitAndReadAControlTransaction()
    {
        using var fixture = new ControlFixture();
        await using var listener = fixture.Listen();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var tls = await FogTlsClient.ConnectAsync(listener.LocalEndpoint, "control.test", FogNodePolicy.SpkiPin(fixture.ServerCertificate), fixture.NodeCertificate, timeout.Token);
        Assert.Equal(SslProtocols.Tls13, tls.SslProtocol);
        using var client = new NinePClient(tls);
        Assert.Equal("9P2000", (await client.VersionAsync(512, "9P2000").WaitAsync(timeout.Token)).Version);
        await client.AttachAsync(1, NinePConstants.NoFid, "worker", "runtime").WaitAsync(timeout.Token);
        await client.WalkAsync(1, 2, ["control", "fixture", "clone"]).WaitAsync(timeout.Token);
        await client.OpenAsync(2, NinePConstants.OREAD).WaitAsync(timeout.Token);
        string id = Encoding.ASCII.GetString((await client.ReadAsync(2, 0, 128).WaitAsync(timeout.Token)).Data.Span).TrimEnd('\n');
        await client.ClunkAsync(2).WaitAsync(timeout.Token);
        await client.WalkAsync(1, 3, ["control", "fixture", id, "request"]).WaitAsync(timeout.Token);
        await client.OpenAsync(3, NinePConstants.OWRITE).WaitAsync(timeout.Token);
        await client.WriteAsync(3, 0, [1, 2, 3]).WaitAsync(timeout.Token);
        Assert.Equal(0, fixture.Effects);
        await client.ClunkAsync(3).WaitAsync(timeout.Token);
        await client.WalkAsync(1, 4, ["control", "fixture", id, "ctl"]).WaitAsync(timeout.Token);
        await client.OpenAsync(4, NinePConstants.OWRITE).WaitAsync(timeout.Token);
        Assert.Equal(7U, (await client.WriteAsync(4, 0, "commit\n"u8.ToArray()).WaitAsync(timeout.Token)).Count);
        await client.WalkAsync(1, 5, ["control", "fixture", id, "reply"]).WaitAsync(timeout.Token);
        await client.OpenAsync(5, NinePConstants.OREAD).WaitAsync(timeout.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await client.ReadAsync(5, 0, 512).WaitAsync(timeout.Token)).Data.ToArray());
        Assert.Equal(1, fixture.Effects);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongServerNameOrPinDoesNotDowngrade(bool wrongName)
    {
        using var fixture = new ControlFixture();
        await using var listener = fixture.Listen();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AuthenticationException>(() => FogTlsClient.ConnectAsync(listener.LocalEndpoint,
            wrongName ? "imposter.test" : "control.test", wrongName ? FogNodePolicy.SpkiPin(fixture.ServerCertificate) : new string('0', 64),
            fixture.NodeCertificate, timeout.Token));
        Assert.Equal(0, fixture.Effects);
    }
}
