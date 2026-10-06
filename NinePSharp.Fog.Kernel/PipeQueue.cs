namespace NinePSharp.Fog.Kernel;

// One direction of a pipe: qio's queue without Qmsg or Qcoalesce.
internal sealed class PipeQueue
{
    public const string Hungup = "i/o on hungup channel";
    public const int MaxLimit = 256 * 1024;
    private const int Atomic = 64 * 1024;
    private readonly object gate = new();
    private readonly LinkedList<byte[]> blocks = new();
    private TaskCompletionSource changed = NewSignal();
    private long read;
    private long written;
    private bool closed;
    private int eof;
    private int limit = MaxLimit;

    // qlen: what is queued for the reader.
    public long Length
    {
        get
        {
            lock (gate)
            {
                return written - read;
            }
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(int count, CancellationToken cancellationToken)
    {
        for (; ;)
        {
            Task wait;
            lock (gate)
            {
                if (blocks.First is { Value: byte[] block })
                {
                    blocks.RemoveFirst();
                    if (block.Length > count)
                    {
                        blocks.AddFirst(block[count..]);
                        block = block[..count];
                    }

                    read += block.Length;
                    Signal();
                    return block;
                }

                if (closed)
                {
                    return eof++ < 3 ? ReadOnlyMemory<byte>.Empty : throw new IOException(Hungup);
                }

                wait = changed.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        int sofar = 0;
        do
        {
            int n = Math.Min(data.Length - sofar, Atomic);
            long end;
            lock (gate)
            {
                if (closed)
                {
                    throw new PipeClosedException();
                }

                blocks.AddLast(data.Slice(sofar, n).ToArray());
                written += n;
                end = written;
                Signal();
            }

            sofar += n;
            await FlowAsync(end, cancellationToken);
        }
        while (sofar < data.Length);
        return data.Length;
    }

    public void Hangup()
    {
        lock (gate)
        {
            closed = true;
            Signal();
        }
    }

    // qsetlimit: writers wait once more than the limit is queued; waiting writers look again.
    public void SetLimit(int bytes)
    {
        lock (gate)
        {
            limit = bytes;
            Signal();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // qflow: the writer waits until no more than the limit is queued ahead of the end of its write.
    private async ValueTask FlowAsync(long end, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (end - read <= limit || closed)
                {
                    return;
                }

                wait = changed.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    private void Signal()
    {
        TaskCompletionSource previous = changed;
        changed = NewSignal();
        previous.SetResult();
    }
}
