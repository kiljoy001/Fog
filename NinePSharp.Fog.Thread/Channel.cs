namespace NinePSharp.Fog.Thread;

/// <summary>The untyped half of a libthread Channel: its buffer counters, its waiting alts, and chanclose.</summary>
public abstract class Channel
{
    private protected Channel(RendezvousGroup group, int size)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        Group = group;
        Size = size;
    }

    /// <summary>The number of messages the channel buffers; zero for an unbuffered channel.</summary>
    public int Size { get; }

    internal RendezvousGroup Group { get; }

    internal int Count { get; set; }

    internal int First { get; set; }

    internal bool Closed { get; set; }

    internal List<Alt> Entries { get; } = new();

    /// <summary>chanclose: no more sends. Wakes blocked senders, and blocked receivers once nothing is buffered.</summary>
    /// <returns>0, or -1 if the channel was already closed.</returns>
    public async Task<int> CloseAsync()
    {
        var failed = new List<Tag>();
        lock (Group.ChannelLock)
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
            await Group.Rendezvous.MeetAsync(tag, Tag.ClosedWake);
        }

        return 0;
    }

    /// <summary>chanclosing: -1 while the channel is open, else the number of messages still buffered.</summary>
    public int Closing()
    {
        lock (Group.ChannelLock)
        {
            return Closed ? Count : -1;
        }
    }

    internal bool IsOpenFor(AltOp op) => !Closed || (op == AltOp.Recv && Count > 0);
}
