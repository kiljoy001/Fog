using System.Buffers.Binary;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class SequentialClientTests
{
    [Fact]
    public async Task ConstructorRejectsNullButAcceptsTheInclusiveConfiguredSizeBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => new NinePSequentialClient(null!));
        using var stream = new MemoryStream();
        await using var client = new NinePSequentialClient(stream, int.MaxValue);
        Assert.Equal((uint)int.MaxValue, client.MessageSize);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(2147483648U)]
    public void MessageAllowanceIsChecked(uint size)
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new NinePSequentialClient(stream, size));
    }

    [Theory]
    [InlineData("9P2000", 255U)]
    [InlineData("9P2000", 513U)]
    [InlineData("9P2000.L", 512U)]
    [InlineData("unknown", 512U)]
    public async Task InvalidNegotiationTerminatesTransport(string version, uint size)
    {
        using var stream = new PeerStream(Frame(new Rversion(65535, size, version)));
        await using var client = new NinePSequentialClient(stream, 512);
        Assert.Equal("Invalid 9P negotiation.", (await Assert.ThrowsAsync<IOException>(() => client.NegotiateAsync(CancellationToken.None))).Message);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("size-low", "Invalid response frame.")]
    [InlineData("size-high", "Invalid response frame.")]
    [InlineData("tag", "Invalid response frame.")]
    [InlineData("type", "Unexpected 9P response.")]
    [InlineData("truncated", null)]
    [InlineData("trailing", "Noncanonical or mismatched 9P response.")]
    [InlineData("mismatched", "Noncanonical or mismatched 9P response.")]
    public async Task MalformedReplyCannotDesynchronizeTheFollowingExchange(string kind, string? diagnostic)
    {
        byte[] reply = Frame(new Rclunk(0));
        switch (kind)
        {
            case "size-low": BinaryPrimitives.WriteUInt32LittleEndian(reply, 6); break;
            case "size-high": BinaryPrimitives.WriteUInt32LittleEndian(reply, 513); break;
            case "tag": reply[5] = 1; break;
            case "type": reply[4] = 254; break;
            case "truncated": reply = reply[..^1]; break;
            case "trailing": reply = reply.Concat(new byte[] { 1 }).ToArray(); reply[0]++; break;
            case "mismatched": reply = Frame(new Rflush(0)); break;
        }
        using var stream = new PeerStream(Frame(new Rversion(65535, 512, "9P2000")).Concat(reply).ToArray());
        await using var client = new NinePSequentialClient(stream, 512);
        await client.NegotiateAsync(CancellationToken.None);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, 1), CancellationToken.None));
        if (diagnostic is not null) Assert.Equal(diagnostic, error.Message);
        Assert.True(stream.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, 1), CancellationToken.None));
    }

    [Fact]
    public async Task RerrorIsSemanticAndDoesNotPoisonTheNextSequentialTag()
    {
        using var stream = new PeerStream(Frame(new Rversion(65535, 256, "9P2000"))
            .Concat(Frame(new Rerror(0, "denied"))).Concat(Frame(new Rclunk(1))).ToArray());
        await using var client = new NinePSequentialClient(stream, 512);
        await client.NegotiateAsync(CancellationToken.None);
        Assert.Equal(256U, client.MessageSize);
        Assert.Equal("denied", (await Assert.ThrowsAsync<NinePException>(() =>
            client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, 1), CancellationToken.None))).Message);
        Assert.False(stream.Disposed);
        Assert.Equal((ushort)1, (await client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, 1), CancellationToken.None)).Tag);
        Assert.Equal(Frame(new Tversion(65535, 512, "9P2000")).Concat(Frame(new Tclunk(0, 1))).Concat(Frame(new Tclunk(1, 1))), stream.Written);
    }

    [Fact]
    public async Task RequestsAreBoundedBeforeWritingAndCancellationBeforeAdmissionDoesNotSend()
    {
        using var stream = new PeerStream(Frame(new Rversion(65535, 256, "9P2000")));
        await using var client = new NinePSequentialClient(stream, 256);
        Assert.Equal("Negotiate before exchanging requests.", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, 1), CancellationToken.None))).Message);
        Assert.Empty(stream.Written);
        await client.NegotiateAsync(CancellationToken.None);
        Assert.Equal("Invalid request frame.", (await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExchangeAsync<Rclunk>(_ => new Tclunk(65535, 1), CancellationToken.None))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExchangeAsync<Rwrite>(tag => new Twrite(tag, 1, 0, new byte[256]), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, 1), cancellation.Token));
        Assert.Equal(Frame(new Tversion(65535, 256, "9P2000")), stream.Written);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task ARequestCanFillTheNegotiatedFrameAndEachExchangeFlushesItsBytes()
    {
        using var stream = new PeerStream(Frame(new Rversion(65535, 256, "9P2000")).Concat(Frame(new Rwrite(0, 233))).ToArray());
        await using var client = new NinePSequentialClient(stream, 256);
        await client.NegotiateAsync(CancellationToken.None);
        byte[] payload = Enumerable.Range(0, 233).Select(value => (byte)value).ToArray();
        var reply = await client.ExchangeAsync<Rwrite>(tag => new Twrite(tag, 1, 0, payload), CancellationToken.None);
        Assert.Equal(233U, reply.Count);
        Assert.Equal(Frame(new Tversion(65535, 256, "9P2000")).Concat(Frame(new Twrite(0, 1, 0, payload))), stream.Written);
        Assert.Equal(2, stream.Flushes);
    }

    [Fact]
    public async Task SevenByteRequestFrameIsAnInclusiveProtocolBoundary()
    {
        using var stream = new PeerStream(Frame(new Rversion(65535, 256, "9P2000")).Concat(Frame(new Rclunk(0))).ToArray());
        await using var client = new NinePSequentialClient(stream, 256);
        await client.NegotiateAsync(CancellationToken.None);

        Assert.IsType<Rclunk>(await client.ExchangeAsync<Rclunk>(tag => new MinimumRequest(tag), CancellationToken.None));
    }

    [Fact]
    public async Task NegotiationDoesNotRequireTheCallersSynchronizationContextAfterAnAsyncWrite()
    {
        using var stream = new PeerStream(Frame(new Rversion(65535, 256, "9P2000")))
        {
            WriteResume = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var client = new NinePSequentialClient(stream, 256);
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        Task negotiation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            negotiation = client.NegotiateAsync(CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.False(negotiation.IsCompleted);
        stream.WriteResume.SetResult();
        await negotiation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, context.Posts);
        Assert.Equal(256U, client.MessageSize);
    }

    [Theory]
    [InlineData("flush")]
    [InlineData("header")]
    [InlineData("body")]
    [InlineData("dispose")]
    public async Task EveryNegotiationAwaitAvoidsTheCallersSynchronizationContext(string stage)
    {
        bool invalid = stage == "dispose";
        using var stream = new PeerStream(Frame(new Rversion(65535, invalid ? 255U : 256U, "9P2000")));
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stage == "flush") stream.FlushResume = resume;
        if (stage is "header" or "body")
        {
            stream.ReadResume = resume;
            stream.PauseBodyRead = stage == "body";
        }
        if (stage == "dispose") stream.DisposeResume = resume;
        await using var client = new NinePSequentialClient(stream, 256);
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        Task negotiation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            negotiation = client.NegotiateAsync(CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.False(negotiation.IsCompleted);

        resume.SetResult();
        if (invalid) await Assert.ThrowsAsync<IOException>(() => negotiation.WaitAsync(TimeSpan.FromMilliseconds(250)));
        else await negotiation.WaitAsync(TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, context.Posts);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    internal static byte[] Frame(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }

    private readonly struct MinimumRequest(ushort tag) : ISerializable
    {
        public uint Size => 7;
        public MessageTypes Type => MessageTypes.Tclunk;
        public ushort Tag => tag;
        public void WriteTo(Span<byte> data)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, Size);
            data[4] = (byte)Type;
            BinaryPrimitives.WriteUInt16LittleEndian(data[5..], Tag);
        }
    }

    private sealed class PeerStream(byte[] response) : Stream
    {
        private readonly MemoryStream reads = new(response);
        private readonly MemoryStream writes = new();
        internal bool Disposed;
        internal int Flushes;
        internal TaskCompletionSource? WriteResume;
        internal TaskCompletionSource? FlushResume;
        internal TaskCompletionSource? ReadResume;
        internal TaskCompletionSource? DisposeResume;
        internal bool PauseBodyRead;
        internal byte[] Written => writes.ToArray();
        public override bool CanRead => !Disposed;
        public override bool CanWrite => !Disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => reads.Read(buffer, offset, Math.Min(count, 2));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (ReadResume is not null && (PauseBodyRead ? reads.Position >= 7 : reads.Position == 0))
                await ReadResume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await reads.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken).ConfigureAwait(false);
        }
        public override void Write(byte[] buffer, int offset, int count) => writes.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (WriteResume is not null) await WriteResume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await writes.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override void Flush() => Flushes++;
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (FlushResume is not null) await FlushResume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Flushes++;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) { reads.Dispose(); writes.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync()
        {
            if (DisposeResume is not null) await DisposeResume.Task.ConfigureAwait(false);
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
