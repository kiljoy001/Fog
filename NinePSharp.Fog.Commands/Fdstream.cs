using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// A descriptor as a stream, for a library that reads and writes one, as Dp9ik.Tls's TLS does. Its
// reads are made by Reader and its writes by Writer, which may become other processes holding the
// same descriptor. A read the conversation fails ends the stream as a hangup does.
public sealed class Fdstream(Process process, int fd) : Stream
{
    public Process Reader { get; set; } = process;

    public Process Writer { get; set; } = process;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // A read that fails is the stream's end.
        ReadOnlyMemory<byte> read;
        try
        {
            read = await Reader.ReadAsync(fd, buffer.Length, cancellationToken);
        }
        catch (SyscallException)
        {
            read = ReadOnlyMemory<byte>.Empty;
        }

        read.CopyTo(buffer);
        return read.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await Writer.WriteAsync(fd, buffer.ToArray(), cancellationToken);
        }
        catch (SyscallException error)
        {
            throw new IOException(error.Message, error);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
