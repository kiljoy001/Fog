using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// libbio's Biobuf for reading, as far as Brdline goes: a line, its delimiter included, from a
// buffer of Bsize bytes, or null at the end of input, before a last line that has no delimiter,
// or at a line too long for the buffer.
public sealed class Biobuf(Process process, int fd)
{
    public const int Bsize = 8192;

    private readonly List<byte> buffer = [];
    private bool eof;

    public async Task<byte[]?> BrdlineAsync(byte delim)
    {
        int scanned = 0;
        while (true)
        {
            int at = buffer.IndexOf(delim, scanned);
            if (at >= 0)
            {
                byte[] line = buffer.GetRange(0, at + 1).ToArray();
                buffer.RemoveRange(0, at + 1);
                return line;
            }

            if (eof || buffer.Count >= Bsize)
            {
                return null;
            }

            scanned = buffer.Count;
            ReadOnlyMemory<byte> read;
            try
            {
                read = await process.ReadAsync(fd, Bsize - buffer.Count);
            }
            catch (SyscallException)
            {
                read = ReadOnlyMemory<byte>.Empty;
            }

            eof = read.IsEmpty;
            buffer.AddRange(read.ToArray());
        }
    }
}
