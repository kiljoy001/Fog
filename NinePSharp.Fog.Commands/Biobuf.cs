using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// libbio's Biobuf. For reading, Brdline and Bread: Brdline gives a line, its delimiter included,
// from a buffer of Bsize bytes, or null at the end of input, before a last line that has no
// delimiter, or at a line too long for the buffer, which Bgetc then reads; Bread reads until it
// has what it was asked for or the input ends. For writing, Bwrite and Bflush, which the program
// calls before it exits as libbio's atexit does. Errors go to Errorf, as Blethal sets it, and Iof runs before each read,
// as a Biofn of the program's that then reads.
public sealed class Biobuf(Process process, int fd)
{
    // IOUNIT, as 9front's bio.h has it.
    public const int Bsize = 32768;
    public const int Beof = -1;

    // The bytes Bgetc keeps from the buffer before for Bungetc.
    private const int Bungetsize = 4;

    private readonly List<byte> buffer = [];
    private readonly byte[] obuf = new byte[Bsize];
    private bool eof;
    private bool readFailed;
    private int ocount;
    private bool inactive;
    private int gpos;
    private bool bracteof;

    public Func<string, Task>? Errorf { get; set; }

    public Func<Task>? Iof { get; set; }

    // Blinelen: the length of the line Brdline last gave, or of what it left unread when it gave
    // none, as Bgetc then reads it.
    public int Blinelen { get; private set; }

    public async Task<byte[]?> BrdlineAsync(byte delim)
    {
        // What Bgetc has given is gone.
        buffer.RemoveRange(0, gpos);
        gpos = 0;
        int scanned = 0;
        while (true)
        {
            int at = buffer.IndexOf(delim, scanned);
            if (at >= 0)
            {
                byte[] line = buffer.GetRange(0, at + 1).ToArray();
                buffer.RemoveRange(0, at + 1);
                Blinelen = line.Length;
                return line;
            }

            if (eof || buffer.Count >= Bsize)
            {
                Blinelen = buffer.Count;
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

    // Bread: up to count bytes into p, or Beof when a read failed before any came; once the input
    // has ended, or a read failed, it is not read again.
    public async Task<int> BreadAsync(byte[] p, int count)
    {
        int c = 0;
        while (c < count)
        {
            if (buffer.Count == 0)
            {
                if (eof)
                {
                    break;
                }

                ReadOnlyMemory<byte> read;
                try
                {
                    await (Iof?.Invoke() ?? Task.CompletedTask);
                    read = await process.ReadAsync(fd, Bsize);
                }
                catch (SyscallException error)
                {
                    Errstr.Set(process, error.Message);
                    eof = true;
                    await BerrorAsync($"read error: {error.Message}");
                    readFailed = true;
                    break;
                }

                eof = read.IsEmpty;
                buffer.AddRange(read.ToArray());
                continue;
            }

            int n = Math.Min(count - c, buffer.Count);
            buffer.CopyTo(0, p, c, n);
            buffer.RemoveRange(0, n);
            c += n;
        }

        return c == 0 && readFailed ? Beof : c;
    }

    // Bgetc: the next byte, or Beof at the end of input, which it gives once more before it reads
    // again, or once a read has failed.
    public async Task<int> BgetcAsync()
    {
        if (gpos < buffer.Count)
        {
            return buffer[gpos++];
        }

        if (bracteof || readFailed)
        {
            bracteof = false;
            return Beof;
        }

        buffer.RemoveRange(0, Math.Max(0, buffer.Count - Bungetsize));
        gpos = buffer.Count;
        ReadOnlyMemory<byte> read;
        try
        {
            await (Iof?.Invoke() ?? Task.CompletedTask);
            read = await process.ReadAsync(fd, Bsize);
        }
        catch (SyscallException error)
        {
            Errstr.Set(process, error.Message);
            readFailed = true;
            await BerrorAsync($"read error: {error.Message}");
            return Beof;
        }

        if (read.IsEmpty)
        {
            bracteof = true;
            return Beof;
        }

        buffer.AddRange(read.ToArray());
        return buffer[gpos++];
    }

    // Bungetc: the last byte Bgetc gave back to it.
    public void Bungetc()
    {
        bracteof = false;
        gpos -= readFailed ? 0 : 1;
    }

    // Bgetrune: the next rune, the bytes fullrune says it needs read, and those of a bad one past
    // its first given back; or Beof, where the input ends, even in a rune.
    public async Task<long> BgetruneAsync()
    {
        int c = await BgetcAsync();
        if (c < Utf.RuneSelf)
        {
            return c;
        }

        var str = new List<byte> { (byte)c };
        for (; ;)
        {
            if ((c = await BgetcAsync()) < 0)
            {
                return c;
            }

            str.Add((byte)c);
            if (Utf.FullRune(str.ToArray()))
            {
                int size = Utf.CharToRune(out uint rune, str.ToArray());
                for (int i = str.Count; i > size; i--)
                {
                    Bungetc();
                }

                return rune;
            }
        }
    }

    // Bputc: a byte into the buffer, which is written when it is full; Beof when that fails.
    public async Task<int> BputcAsync(byte c)
    {
        for (; ;)
        {
            if (ocount < Bsize)
            {
                obuf[ocount++] = c;
                return 0;
            }

            if (await BflushAsync() == Beof)
            {
                return Beof;
            }
        }
    }

    // Bputrune: a rune's UTF-8.
    public async Task<long> BputruneAsync(uint r)
    {
        if (r < Utf.RuneSelf)
        {
            await BputcAsync((byte)r);
            return 1;
        }

        byte[] str = new byte[Utf.UtfMax];
        int n = Utf.RuneToChar(str, r);
        return await BwriteAsync(str.AsMemory(0, n)) != n ? Beof : n;
    }

    // Bwrite: the bytes put in the buffer, a full one written whole before more go in, or Beof
    // when that write fails, after which the buffer takes no more unless an interrupt failed it.
    public async Task<long> BwriteAsync(ReadOnlyMemory<byte> data)
    {
        int oc = ocount;
        for (int p = 0; p < data.Length;)
        {
            int n = Math.Min(Bsize - oc, data.Length - p);
            if (n == 0)
            {
                if (inactive)
                {
                    return Beof;
                }

                if (await WriteAsync(obuf) is { } error)
                {
                    if (!error.Contains("interrupt", StringComparison.Ordinal))
                    {
                        inactive = true;
                        await BerrorAsync($"write error: {error}");
                    }

                    return Beof;
                }

                oc = 0;
                continue;
            }

            data.Slice(p, n).CopyTo(obuf.AsMemory(oc));
            oc += n;
            p += n;
        }

        ocount = oc;
        return data.Length;
    }

    // Bflush: what the buffer holds written, or Beof when that fails or a write has failed before.
    public async Task<int> BflushAsync()
    {
        if (inactive)
        {
            return Beof;
        }

        if (ocount == 0)
        {
            return 0;
        }

        if (await WriteAsync(obuf.AsMemory(0, ocount)) is not { } error)
        {
            ocount = 0;
            return 0;
        }

        (inactive, ocount) = (true, Bsize);
        await BerrorAsync($"write error: {error}");
        return Beof;
    }

    private Task BerrorAsync(string message) => Errorf?.Invoke(message) ?? Task.CompletedTask;

    // A write of all of data, or the error that kept it short, left in errstr.
    private async Task<string?> WriteAsync(ReadOnlyMemory<byte> data)
    {
        try
        {
            return await process.WriteAsync(fd, data) == data.Length ? null : Errstr.Get(process);
        }
        catch (SyscallException error)
        {
            Errstr.Set(process, error.Message);
            return error.Message;
        }
    }
}
