using System.Text;

namespace NinePSharp.Fog.Rc.Tests.Support;

// A stream that keeps what is written to it, for its text to be read while it is written.
public sealed class Recorded : Stream
{
    private readonly List<byte> written = [];

    public string Text
    {
        get
        {
            lock (written)
            {
                return Encoding.UTF8.GetString([.. written]);
            }
        }
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (written)
        {
            written.AddRange(buffer.AsSpan(offset, count));
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (written)
        {
            written.AddRange(buffer.Span);
        }

        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
