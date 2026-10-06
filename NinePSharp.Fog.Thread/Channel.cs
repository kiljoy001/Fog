namespace NinePSharp.Fog.Thread;

/// <summary>The untyped half of a libthread Channel: its buffer counters, its waiting alts, and chanclose.</summary>
public abstract class Channel
{
    // channel.c's chanlock: one lock for every channel, so an alt can examine all of its channels at once.
    internal static readonly object Lock = new();

    private protected Channel(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        Size = size;
    }

    /// <summary>The number of messages the channel buffers; zero for an unbuffered channel.</summary>
    public int Size { get; }

    internal int Count { get; set; }

    internal int First { get; set; }

    internal bool Closed { get; set; }

    internal List<Alt> Entries { get; } = new();

    /// <summary>chanclose: no more sends. Wakes blocked senders, and blocked receivers once nothing is buffered.</summary>
    /// <returns>0, or -1 if the channel was already closed.</returns>
    public async Task<int> CloseAsync()
    {
        var failed = new List<Tag>();
        lock (Lock)
        {
            if (Closed)
            {
                return -1;
            }

            Closed = true;

            // channel.c wakes waiting senders, and receivers if nothing is buffered. An uncommitted
            // receiver only ever waits on an empty buffer, since a send meets a waiting receiver rather
            // than buffering, so every uncommitted waiter fails; a committed one is met by its partner.
            foreach (Alt entry in Entries)
            {
                if (entry.Tag!.Taken)
                {
                    continue;
                }

                entry.Tag.Committed = this;
                failed.Add(entry.Tag);
            }
        }

        foreach (Tag tag in failed)
        {
            await Rendezvous.MeetAsync(tag, Tag.ClosedWake);
        }

        return 0;
    }

    /// <summary>chanclosing: -1 while the channel is open, else the number of messages still buffered.</summary>
    public int Closing()
    {
        lock (Lock)
        {
            return Closed ? Count : -1;
        }
    }

    internal bool IsOpenFor(AltOp op) => !Closed || (op == AltOp.Recv && Count > 0);
}
