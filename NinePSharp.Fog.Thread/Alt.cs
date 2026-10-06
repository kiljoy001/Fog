namespace NinePSharp.Fog.Thread;

/// <summary>
/// One entry of a libthread alt: a send, a recv, or no operation. Alt executes one entry, chosen at random
/// among those that can proceed, as channel.c's alt does; send and recv are alts of one entry.
/// </summary>
public abstract class Alt
{
    internal const string ClosedError = "channel was closed";

    private protected Alt(Channel? channel, AltOp op)
    {
        Channel = channel;
        Op = op;
    }

    /// <summary>CHANNOP: an entry alt skips, for alts over a varying set of operations.</summary>
    public static Alt Nop { get; } = new NoOperation();

    /// <summary>The Alt structure's err: set when the entry was executed by a close; kept between alts.</summary>
    public string? Error { get; internal set; }

    internal Channel? Channel { get; }

    internal AltOp Op { get; }

    internal Tag? Tag { get; set; }

    /// <summary>alt ending in CHANEND: the index of the entry executed, or -1 if interrupted or every entry failed by a close.</summary>
    public static async Task<int> AltAsync(IReadOnlyList<Alt> alts, CancellationToken interrupt = default)
    {
        RendezvousGroup group = GroupOf(alts);
        int now;
        Tag? waiting;
        Tag? met;
        lock (group.ChannelLock)
        {
            now = Now(alts, block: true, out waiting, out met);
        }

        if (met is not null)
        {
            await group.Rendezvous.MeetAsync(met, null, CancellationToken.None);
            return now;
        }

        return waiting is null ? now : await WaitAsync(group, alts, waiting, interrupt);
    }

    /// <summary>alt ending in CHANNOBLK: as AltAsync, but the terminator's index, alts.Count, rather than blocking.</summary>
    public static async Task<int> NbAltAsync(IReadOnlyList<Alt> alts)
    {
        RendezvousGroup group = GroupOf(alts);
        int now;
        Tag? met;
        lock (group.ChannelLock)
        {
            now = Now(alts, block: false, out _, out met);
        }

        if (met is not null)
        {
            await group.Rendezvous.MeetAsync(met, null, CancellationToken.None);
        }

        return now;
    }

    // Copies a message between this entry and a waiting entry of the other op on the same channel.
    internal abstract void Meet(Alt waiter);

    // Takes a message from, or puts one in, the channel's buffer.
    internal abstract void UseBuffer();

    // An alt's channels share one group, as a libthread program's channels share its process; an alt of
    // CHANNOP entries alone has no channel to meet on and waits in a group of its own.
    private static RendezvousGroup GroupOf(IReadOnlyList<Alt> alts)
    {
        RendezvousGroup[] groups = alts.Where(entry => entry.Op != AltOp.Nop).Select(entry => entry.Channel!.Group).Distinct().ToArray();
        return groups.Length switch
        {
            0 => new RendezvousGroup(),
            1 => groups[0],
            _ => throw new ArgumentException("an alt's channels must share a rendezvous group", nameof(alts)),
        };
    }

    // Under the lock: executes an entry that can proceed, returning the tag of any waiter it committed;
    // or settles what a closed set of channels means; or queues the alt on every open channel.
    private static int Now(IReadOnlyList<Alt> alts, bool block, out Tag? waiting, out Tag? met)
    {
        waiting = null;
        met = null;
        int ready = 0;
        int chosen = -1;
        for (int i = 0; i < alts.Count; i++)
        {
            Alt entry = alts[i];
            if (entry.Op != AltOp.Nop && entry.Channel!.IsOpenFor(entry.Op) && entry.CanExecute() && Random.Shared.Next(++ready) == 0)
            {
                chosen = i;
            }
        }

        if (chosen >= 0)
        {
            met = alts[chosen].Execute();
            return chosen;
        }

        return block ? Queue(alts, out waiting) : alts.Count;
    }

    private static int Queue(IReadOnlyList<Alt> alts, out Tag? waiting)
    {
        var tag = new Tag();
        bool open = false;
        bool alreadyClosed = false;
        int closed = -1;
        for (int i = 0; i < alts.Count; i++)
        {
            Alt entry = alts[i];
            if (entry.Op == AltOp.Nop)
            {
                continue;
            }

            if (entry.Channel!.IsOpenFor(entry.Op))
            {
                open = true;
                entry.Tag = tag;
                entry.Channel.Entries.Add(entry);
            }
            else if (entry.Error != ClosedError)
            {
                closed = i;
            }
            else
            {
                alreadyClosed = true;
            }
        }

        waiting = tag;
        if (open)
        {
            return 0;
        }

        // Everything was closed: select the last entry not yet failed by its close, or fail.
        waiting = null;
        if (closed >= 0)
        {
            alts[closed].Error = ClosedError;
            return closed;
        }

        // Only CHANNOP entries would leave the alt waiting forever, as they do in libthread.
        if (!alreadyClosed)
        {
            waiting = tag;
            return 0;
        }

        return -1;
    }

    private static async Task<int> WaitAsync(RendezvousGroup group, IReadOnlyList<Alt> alts, Tag tag, CancellationToken interrupt)
    {
        object? woken = await group.Rendezvous.MeetAsync(tag, null, interrupt);
        while (true)
        {
            lock (group.ChannelLock)
            {
                if (woken != Rendezvous.Interrupted)
                {
                    return Leave(alts, tag, woken);
                }

                if (!tag.Taken)
                {
                    tag.Committed = Tag.Nowhere;
                    return Leave(alts, tag, woken);
                }
            }

            // Interrupted after someone committed us: they will meet us, so go back (channel.c's Again).
            woken = await group.Rendezvous.MeetAsync(tag, null, CancellationToken.None);
        }
    }

    // Leave every channel, and report the entry whose channel committed us.
    private static int Leave(IReadOnlyList<Alt> alts, Tag tag, object? woken)
    {
        int chosen = -1;
        for (int i = 0; i < alts.Count; i++)
        {
            Alt entry = alts[i];
            if (entry.Op == AltOp.Nop)
            {
                continue;
            }

            if (entry.Channel == tag.Committed)
            {
                chosen = i;
                entry.Error = woken == Tag.ClosedWake ? ClosedError : null;
            }

            entry.Channel!.Entries.Remove(entry);
            entry.Tag = null;
        }

        return chosen;
    }

    // Is a waiting entry of the other op uncommitted, or is there room or a message in the buffer?
    private bool CanExecute()
    {
        AltOp other = Op == AltOp.Send ? AltOp.Recv : AltOp.Send;
        return Channel!.Entries.Exists(entry => entry.Op == other && !entry.Tag!.Taken) ||
            (Op == AltOp.Send ? Channel.Count < Channel.Size : Channel.Count > 0);
    }

    // channel.c's altexec: meet a waiting entry chosen at random and commit it, or use the buffer.
    private Tag? Execute()
    {
        AltOp other = Op == AltOp.Send ? AltOp.Recv : AltOp.Send;
        Alt? waiter = null;
        int ready = 0;
        foreach (Alt entry in Channel!.Entries)
        {
            if (entry.Op == other && !entry.Tag!.Taken && Random.Shared.Next(++ready) == 0)
            {
                waiter = entry;
            }
        }

        if (waiter is null)
        {
            UseBuffer();
            return null;
        }

        Meet(waiter);
        waiter.Tag!.Committed = Channel;
        return waiter.Tag;
    }

    private sealed class NoOperation() : Alt(null, AltOp.Nop)
    {
        internal override void Meet(Alt waiter) => throw new InvalidOperationException();

        internal override void UseBuffer() => throw new InvalidOperationException();
    }
}
