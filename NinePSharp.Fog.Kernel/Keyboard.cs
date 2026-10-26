namespace NinePSharp.Fog.Kernel;

// drawterm's kbd and its queues: the console's input, which #c/cons reads as drawterm's devcons reads
// its standard input when it has no screen. A cooked read gathers a line in line, backspace and ^U
// editing it, and a newline, ^D or a full line ending it in lineq; a raw read gives what is typed as
// it comes. kbdq holds what of a line was typed when consctl was written rawon. The host's terminal
// is raw while kbd is.
internal sealed class Keyboard(HostConsole console)
{
    private readonly SemaphoreSlim lk = new(1, 1);
    private readonly LinkedList<byte[]> kbdq = new();
    private readonly LinkedList<byte[]> lineq = new();
    private readonly byte[] line = new byte[1024];
    private bool raw;
    private int ctl;
    private int x;

    // consread's Qcons.
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(int n, CancellationToken cancellationToken)
    {
        byte[] buf = new byte[n];
        await lk.WaitAsync(cancellationToken);
        try
        {
            if (raw)
            {
                if (lineq.Count != 0)
                {
                    n = Qread(lineq, buf);
                }
                else
                {
                    // As much as there is: drawterm reads on while kbdq holds more, but Fog's kbdq,
                    // with no screen's keyboard to fill it, holds at most what rawon kept of a line,
                    // so one read gives all of it or all that was asked for.
                    n = await QreadconsAsync(kbdq, buf, cancellationToken);
                }
            }
            else
            {
                while (lineq.Count == 0)
                {
                    bool eol = true;
                    if (await QreadconsAsync(kbdq, line.AsMemory(x, 1), cancellationToken) == 1)
                    {
                        eol = false;
                        switch (line[x])
                        {
                            case (byte)'\b':
                                x -= x > 0 ? 1 : 0;
                                break;
                            case 0x15:
                                x = 0;
                                break;
                            case (byte)'\n':
                                x++;
                                eol = true;
                                break;
                            case 0x04:
                                eol = true;
                                break;
                            default:
                                x++;
                                break;
                        }
                    }

                    if (x == line.Length || eol)
                    {
                        lineq.AddLast(line[..x]);
                        x = 0;
                    }
                }

                n = Qread(lineq, buf);
            }
        }
        finally
        {
            lk.Release();
        }

        return buf.AsMemory(0, n);
    }

    // consopen's Qconsctl.
    public async ValueTask OpenctlAsync(CancellationToken cancellationToken)
    {
        await lk.WaitAsync(cancellationToken);
        ctl++;
        lk.Release();
    }

    // consclose's Qconsctl: the last close turns off raw.
    public async ValueTask ClosectlAsync()
    {
        await lk.WaitAsync();
        if (--ctl == 0)
        {
            raw = false;
        }

        console.SetTerm(raw);
        lk.Release();
    }

    // conswrite's Qconsctl: rawon, keeping what was typed of a line for the next read, and rawoff,
    // dropping it, wherever each begins a word of the first 255 bytes.
    public async ValueTask CtlAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> buf = data[..Math.Min(data.Length, 255)];
        int nul = buf.Span.IndexOf((byte)0);
        buf = nul < 0 ? buf : buf[..nul];
        for (int a = 0; a >= 0;)
        {
            bool rawon = buf.Span[a..].StartsWith("rawon"u8), rawoff = buf.Span[a..].StartsWith("rawoff"u8);
            int space = buf.Span[a..].IndexOf((byte)' ');
            if (rawon)
            {
                await lk.WaitAsync(cancellationToken);
                if (x != 0)
                {
                    kbdq.AddLast(line[..x]);
                    x = 0;
                }

                raw = true;
                console.SetTerm(true);
                lk.Release();
            }
            else if (rawoff)
            {
                await lk.WaitAsync(cancellationToken);
                (raw, x) = (false, 0);
                console.SetTerm(false);
                lk.Release();
            }

            a = space < 0 ? -1 : a + space + 1;
        }
    }

    // qread of a queue that is not a message queue: the first block, the rest of it put back when
    // it is more than was asked for.
    private static int Qread(LinkedList<byte[]> q, Memory<byte> buf)
    {
        byte[] first = q.First!.Value;
        q.RemoveFirst();
        int n = Math.Min(first.Length, buf.Length);
        first.AsSpan(0, n).CopyTo(buf.Span);
        if (n < first.Length)
        {
            q.AddFirst(first[n..]);
        }

        return n;
    }

    // qreadcons: from the queue when it holds something, otherwise from the host's console.
    private async ValueTask<int> QreadconsAsync(LinkedList<byte[]> q, Memory<byte> buf, CancellationToken cancellationToken)
        => q.Count == 0 ? await console.ReadAsync(buf, cancellationToken) : Qread(q, buf);
}
