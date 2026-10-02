using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class ControlRetryTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task AReplyLostAfterAnEffectNeverAllocatesASecondOperation(bool release, int expectedConnections)
    {
        using var fixture = new ControlFixture();
        await using var listener = fixture.Listen();
        int connections = 0;
        var client = new FogTransactionClient(
            async cancellation =>
        {
            var tls = await FogTlsClient.ConnectAsync(
                listener.LocalEndpoint,
                "control.test",
                FogNodePolicy.SpkiPin(fixture.ServerCertificate),
                fixture.NodeCertificate,
                cancellation);
            return ++connections == 1 ? new LoseReplyStream(tls, release ? "release\n"u8.ToArray() : "commit\n"u8.ToArray()) : tls;
        },
            "worker",
            512,
            4096,
            TimeSpan.FromSeconds(1));
        byte[] input = [1, 2, 3];
        var result = await client.ExecuteAsync("fixture", new Dictionary<string, byte[]> { ["request"] = input }, ["reply"]);
        Assert.Equal(input, result["reply"]);
        Assert.Equal(1, fixture.Effects);
        Assert.Equal(expectedConnections, connections);
        Assert.Empty(fixture.Store.LiveIds());
    }

    [Fact]
    public async Task ConnectionFailuresAreBoundedAndCancellationDoesNotStartAConnection()
    {
        int attempts = 0;
        var client = new FogTransactionClient(
            _ =>
            {
                attempts++;
                throw new IOException("offline");
            },
            "worker",
            256,
            1024,
            TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<IOException>(() => client.ExecuteAsync("fixture", new Dictionary<string, byte[]> { ["request"] = [] }, ["reply"])
            .WaitAsync(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(3, attempts);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExecuteAsync("fixture", new Dictionary<string, byte[]> { ["request"] = [] }, ["reply"], cancelled.Token));
        Assert.Equal(3, attempts);
    }

    private sealed class LoseReplyStream(Stream transport, byte[] command) : Stream
    {
        private bool awaitingReply;

        public override bool CanRead => transport.CanRead;

        public override bool CanWrite => transport.CanWrite;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length >= 23 && buffer.Span[4] == (byte)MessageTypes.Twrite)
            {
                awaitingReply = new Twrite(buffer).Data.Span.SequenceEqual(command);
            }

            await transport.WriteAsync(buffer, cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await transport.ReadAsync(buffer, cancellationToken);
            if (awaitingReply)
            {
                await transport.DisposeAsync();
                throw new IOException("injected reply loss");
            }

            return count;
        }

        public override void Flush() => transport.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => transport.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await transport.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                transport.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
