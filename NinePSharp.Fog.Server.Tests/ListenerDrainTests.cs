using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class ListenerDrainTests
{
    private static readonly TimeSpan ShortDrain = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task DisposalForceClosesAConnectionThatIgnoresCancellation()
    {
        var logger = new RecordingLogger();
        var listener = new Stuck(logger);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(listener.LocalEndpoint);
        await listener.Serving.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(["1 connections had not finished and were force-closed; their outcome is unknown."], logger.At(LogLevel.Warning));
        listener.Release.SetResult();
    }

    [Fact]
    public async Task ANodeConnectionWhoseRequestNeverFinishesReleasesItsSlotWithinTheDrain()
    {
        using var fixture = new ControlFixture();
        await using var listener = new FogNodeListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            fixture.ServerCertificate,
            fixture.Policy,
            new AnswersOnlyVersion(),
            new RecordingLogger(),
            1,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(1),
            ShortDrain);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using (Stream first = await Connect(fixture, listener, deadline.Token))
        {
            await Version(first, deadline.Token);
            await Write(first, new Tclunk(2, 1), deadline.Token);
        }

        while (true)
        {
            try
            {
                await using Stream second = await Connect(fixture, listener, deadline.Token);
                await Version(second, deadline.Token);
                return;
            }
            catch (Exception exception) when (exception is IOException or System.Security.Authentication.AuthenticationException)
            {
                await Task.Delay(20, deadline.Token);
            }
        }
    }

    private static async Task<Stream> Connect(ControlFixture fixture, FogNodeListener listener, CancellationToken cancellation) =>
        await FogTlsClient.ConnectAsync(listener.LocalEndpoint, "control.test", FogNodePolicy.SpkiPin(fixture.ServerCertificate), fixture.NodeCertificate, cancellation);

    private static async Task Version(Stream stream, CancellationToken cancellation)
    {
        await Write(stream, new Tversion(1, 8192, "9P2000"), cancellation);
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, cancellation);
        Assert.Equal((byte)MessageTypes.Rversion, header[4]);
    }

    private static async Task Write(Stream stream, ISerializable message, CancellationToken cancellation)
    {
        var bytes = new byte[message.Size];
        message.WriteTo(bytes);
        await stream.WriteAsync(bytes, cancellation);
        await stream.FlushAsync(cancellation);
    }

    // Serves each connection until released, whatever its cancellation says.
    private sealed class Stuck(ILogger logger) : FogListener(new IPEndPoint(IPAddress.Loopback, 0), logger, 2, TimeSpan.FromMinutes(1), ShortDrain)
    {
        internal TaskCompletionSource Serving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private protected override async Task ServeAsync(TcpClient client, CancellationToken lifetime)
        {
            Serving.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class AnswersOnlyVersion : INinePFSDispatcher
    {
        public Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
            => message is NinePMessage.MsgTversion version
                ? Task.FromResult<object>(new Rversion(version.Item.Tag, version.Item.MSize, "9P2000"))
                : new TaskCompletionSource<object>().Task;
    }
}
