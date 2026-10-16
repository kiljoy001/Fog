using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Rc;

// io.c: buffered reading and writing of a descriptor, a string, or bytes in memory. The process
// doing the I/O is passed in, as a forked shell shares these with its parent but not their
// descriptors. Output is kept until flushed and then written in IOUNIT pieces, the writes pchr
// makes as its buffer fills.
internal sealed class RcIo
{
    public const int EndOfFile = -1;
    private const int NBuf = 32768;
    private readonly List<byte> output = new();
    private byte[] input;
    private int next;
    private int end;

    private RcIo(int? fd, byte[] input)
    {
        Fd = fd;
        this.input = input;
        end = input.Length;
    }

    // The descriptor, or null for an io on a string or bytes.
    public int? Fd { get; }

    // openiofd
    public static RcIo OpenFd(int fd) => new(fd, []);

    // openiostr: written to, then read back with CloseStr.
    public static RcIo OpenStr() => new(null, []);

    // openiocore: end of file after the bytes.
    public static RcIo OpenCore(string bytes) => new(null, RcText.Bytes(bytes));

    // A forked shell's copy of an io it reads, holding what was read and not yet used.
    public RcIo Copy() => new(Fd, input) { next = next, end = end };

    public void Pchr(int c) => output.Add((byte)c);

    public void Pstr(string s)
    {
        foreach (char c in s)
        {
            Pchr(c);
        }
    }

    // pquo
    public void Pquo(string s)
    {
        Pchr('\'');
        foreach (char c in s)
        {
            if (c == '\'')
            {
                Pchr(c);
            }

            Pchr(c);
        }

        Pchr('\'');
    }

    // pwrd: quoted when empty or when it holds a byte rc gives meaning to.
    public void Pwrd(string s)
    {
        if (s.Length == 0 || s.Any(c => c <= ' ' || "`^#*[]=|\\?${}()'<>&;".Contains(c)))
        {
            Pquo(s);
        }
        else
        {
            Pstr(s);
        }
    }

    public void Pdec(int n) => Pstr(n.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // pval
    public void Pval(RcWord? a)
    {
        if (a is null)
        {
            return;
        }

        while (a.Next is not null)
        {
            Pwrd(a.Text);
            Pchr(' ');
            a = a.Next;
        }

        Pwrd(a.Text);
    }

    // pfln
    public void Pfln(string? file, int line, string argv0)
    {
        if (file is not null && line != 0)
        {
            Pstr(file);
            Pchr(':');
            Pdec(line);
        }
        else
        {
            Pstr(file ?? argv0);
        }
    }

    // closeiostr
    public string CloseStr() => RcText.String([.. output]);

    // flushio, for an io on a descriptor: false if a write failed, when rc looks for traps.
    public async ValueTask<bool> FlushAsync(Process process)
    {
        byte[] data = [.. output];
        output.Clear();
        bool written = true;
        for (int at = 0; at < data.Length; at += NBuf)
        {
            try
            {
                await process.WriteAsync(Fd!.Value, data.AsMemory(at, Math.Min(NBuf, data.Length - at)));
            }
            catch (SyscallException)
            {
                written = false;
            }
        }

        return written;
    }

    // closeio
    public async ValueTask CloseAsync(Process process)
    {
        if (Fd is { } fd)
        {
            try
            {
                await process.CloseAsync(fd);
            }
            catch (SyscallException)
            {
            }
        }
    }

    // rchr
    public async ValueTask<int> RchrAsync(Process process)
    {
        if (next >= end && !await FillAsync(process))
        {
            return EndOfFile;
        }

        return input[next++];
    }

    // rstr: the next string ending at one of the stop bytes, skipping stop bytes before it, or
    // null at end of file. An empty stop set splits at NUL.
    public async ValueTask<string?> RstrAsync(Process process, string stop)
    {
        bool Stops(int c) => stop.Contains((char)c) || c == 0;
        int c;
        do
        {
            c = await RchrAsync(process);
            if (c == EndOfFile)
            {
                return null;
            }
        }
        while (c != 0 && Stops(c));

        var s = new List<byte>();
        while (c != EndOfFile && !Stops(c))
        {
            s.Add((byte)c);
            c = await RchrAsync(process);
        }

        return RcText.String([.. s]);
    }

    // emptyiobuf: an io on no descriptor is at end of file, and so is one whose read fails, as a
    // note's interrupting it does.
    private async ValueTask<bool> FillAsync(Process process)
    {
        if (Fd is not { } fd)
        {
            return false;
        }

        try
        {
            input = (await process.ReadAsync(fd, NBuf)).ToArray();
        }
        catch (SyscallException)
        {
            input = [];
        }

        next = 0;
        end = input.Length;
        return end > 0;
    }
}
