using System.Diagnostics;
using System.Net.Sockets;

namespace NinePSharp.Fog.Auth.Tests.Support;

/// <summary>
/// Fails a read once a write has gone <see cref="Wait"/> without any reply, so a host that stops answering
/// fails the scenario. An idle client's pending read, such as NinePClient's read loop, never times out.
/// </summary>
internal sealed class DeadlineStream(Socket socket, bool ownsSocket) : Stream
{
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private readonly NetworkStream inner = new(socket, ownsSocket);
    private long unanswered;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Task<int> read = inner.ReadAsync(buffer, cancellationToken).AsTask();
        while (await Task.WhenAny(read, Task.Delay(Poll, cancellationToken)) != read)
        {
            long since = Interlocked.Read(ref unanswered);
            if (since != 0 && Stopwatch.GetElapsedTime(since) > Wait)
            {
                throw new TimeoutException($"No reply arrived within {Wait} of a request.");
            }
        }

        Interlocked.Exchange(ref unanswered, 0);
        return await read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Requested();
        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Requested();
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Requested() => Interlocked.CompareExchange(ref unanswered, Stopwatch.GetTimestamp(), 0);
}
