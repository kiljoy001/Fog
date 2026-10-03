using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class UserListenerTests
{
    [Fact]
    public void ConnectionsAndLifetimeMustBePositive()
    {
        foreach ((int connections, TimeSpan lifetime) in new[] { (0, TimeSpan.FromMinutes(1)), (1, TimeSpan.Zero) })
        {
            Assert.Equal(
                "Invalid user listener configuration.",
                Assert.Throws<ArgumentException>(() => Create(new Recorder(), connections, lifetime)).Message);
        }
    }

    [Fact]
    public async Task ServesPlain9PWithoutACertificateAndClosesTheSession()
    {
        var recorder = new Recorder();
        await using FogUserListener listener = Create(recorder, 4, TimeSpan.FromMinutes(1));
        listener.Start();
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(listener.LocalEndpoint);
            using var client = new NinePClient(tcp.GetStream());
            Assert.Equal("9P2000", (await client.VersionAsync(8192, "9P2000").WaitAsync(TimeSpan.FromSeconds(5))).Version);
            Assert.Equal(QidType.QTAUTH, (await client.AuthAsync(1, "glenda", "/").WaitAsync(TimeSpan.FromSeconds(5))).Aqid.Type);
        }

        await recorder.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([false, false], recorder.Certificates);
    }

    [Fact]
    public async Task AFailingSessionCloseIsLogged()
    {
        var logger = new RecordingLogger();
        await using FogUserListener listener = Create(new Recorder { FailClose = true }, 4, TimeSpan.FromMinutes(1), logger);
        listener.Start();
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(listener.LocalEndpoint);
        }

        await logger.FirstError.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (_, string message, Exception? exception) = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal("A 9P connection failed.", message);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task AnExtraConnectionIsClosedAndTheLifetimeEndsIdleOnesWithoutAnError()
    {
        var logger = new RecordingLogger();
        await using FogUserListener listener = Create(new Recorder(), 1, TimeSpan.FromSeconds(1), logger);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first = new TcpClient();
        await first.ConnectAsync(listener.LocalEndpoint, timeout.Token);
        using var extra = new TcpClient();
        await extra.ConnectAsync(listener.LocalEndpoint, timeout.Token);
        Assert.Equal(0, await extra.GetStream().ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(0, await first.GetStream().ReadAsync(new byte[1], timeout.Token));
        await listener.DisposeAsync();
        Assert.Empty(logger.At(LogLevel.Error));
    }

    private static FogUserListener Create(INinePFSDispatcher dispatcher, int connections, TimeSpan lifetime, ILogger? logger = null) =>
        new(new IPEndPoint(IPAddress.Loopback, 0), dispatcher, logger ?? NullLogger.Instance, connections, lifetime);

    private sealed class Recorder : INinePFSDispatcher, INinePSessionLifecycle
    {
        internal List<bool> Certificates { get; } = new();

        internal bool FailClose { get; init; }

        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
        {
            Certificates.Add(certificate is not null);
            object reply = message switch
            {
                NinePMessage.MsgTversion m => new Rversion(m.Item.Tag, m.Item.MSize, m.Item.Version),
                NinePMessage.MsgTauth m => new Rauth(m.Item.Tag, new Qid(QidType.QTAUTH, 0, 1)),
                _ => new Rerror(NinePConstants.NoTag, "unexpected"),
            };
            return Task.FromResult(reply);
        }

        public Task CloseSessionAsync(string sessionId)
        {
            Closed.TrySetResult();
            return FailClose ? Task.FromException(new InvalidOperationException("close failed")) : Task.CompletedTask;
        }
    }
}
