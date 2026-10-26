using System.Buffers.Binary;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Interfaces;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// libc's fcall: readn, read9pmsg, convM2S and convS2M, as cs and lib9p use them.
public static class Fcall
{
    // readn: n bytes, or fewer at the end.
    public static async Task<byte[]> ReadnAsync(Process process, int fd, int n)
    {
        var all = new List<byte>();
        for (ReadOnlyMemory<byte> more; all.Count < n && !(more = await process.ReadAsync(fd, n - all.Count)).IsEmpty;)
        {
            all.AddRange(more.Span);
        }

        return [.. all];
    }

    // read9pmsg: one whole message, or none at the end; a length that cannot be a message's is an error.
    public static async Task<byte[]> Read9pmsgAsync(Process process, int fd, int max)
    {
        byte[] count = await ReadnAsync(process, fd, 4);
        if (!BinaryPrimitives.TryReadUInt32LittleEndian(count, out uint len))
        {
            return [];
        }

        if (len <= 4 || len > max)
        {
            throw new SyscallException("bad length in 9P2000 message header");
        }

        byte[] rest = await ReadnAsync(process, fd, (int)len - 4);
        return rest.Length < len - 4 ? [] : [.. count, .. rest];
    }

    // convM2S: the request, or null for one cs cannot read.
    public static ISerializable? ConvM2S(byte[] m)
    {
        try
        {
            return (MessageTypes)m[4] switch
            {
                MessageTypes.Tversion => new Tversion(m),
                MessageTypes.Tauth => new Tauth(m),
                MessageTypes.Tflush => new Tflush(m),
                MessageTypes.Tattach => new Tattach(m),
                MessageTypes.Twalk => new Twalk(m),
                MessageTypes.Topen => new Topen(m),
                MessageTypes.Tcreate => new Tcreate(m),
                MessageTypes.Tread => new Tread(m),
                MessageTypes.Twrite => new Twrite(m),
                MessageTypes.Tclunk => new Tclunk(m),
                MessageTypes.Tremove => new Tremove(m),
                MessageTypes.Tstat => new Tstat(m),
                MessageTypes.Twstat => new Twstat(m),
                _ => null,
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A message too short for its type.
        }

        return null;
    }

    public static byte[] ConvS2M(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }
}
