namespace NinePSharp.Fog.Thread;

/// <summary>An alt entry that sends or receives <typeparamref name="T"/> messages; Value holds what it sends or received.</summary>
public sealed class Alt<T> : Alt
{
    private Alt(Channel<T> channel, AltOp op, T? value)
        : base(channel, op) => Value = value;

    public T? Value { get; internal set; }

    private Channel<T> Typed => (Channel<T>)Channel!;

    /// <summary>CHANSND of <paramref name="value"/> on <paramref name="channel"/>.</summary>
    public static Alt<T> Send(Channel<T> channel, T value)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new Alt<T>(channel, AltOp.Send, value);
    }

    /// <summary>CHANRCV from <paramref name="channel"/>.</summary>
    public static Alt<T> Recv(Channel<T> channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new Alt<T>(channel, AltOp.Recv, default);
    }

    internal override void Meet(Alt waiter)
    {
        var other = (Alt<T>)waiter;
        Channel<T> channel = Typed;
        if (channel.Size == 0 || channel.Count == 0)
        {
            if (Op == AltOp.Recv)
            {
                Value = other.Value;
            }
            else
            {
                other.Value = Value;
            }

            return;
        }

        // A full buffer with senders waiting, met by a receiver (a sender would have queued): receive its
        // oldest message, and buffer the waiting sender's message in its place, as altexec does.
        int head = channel.First % channel.Size;
        Value = channel.Buffer[head];
        channel.Buffer[head] = other.Value!;
        channel.First++;
    }

    internal override void UseBuffer()
    {
        Channel<T> channel = Typed;
        if (Op == AltOp.Recv)
        {
            int head = channel.First % channel.Size;
            Value = channel.Buffer[head];
            channel.Buffer[head] = default!;
            channel.Count--;
            channel.First++;
            return;
        }

        channel.Buffer[(channel.First + channel.Count) % channel.Size] = Value!;
        channel.Count++;
    }
}
