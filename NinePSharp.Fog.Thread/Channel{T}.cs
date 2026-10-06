namespace NinePSharp.Fog.Thread;

/// <summary>A libthread Channel of <typeparamref name="T"/> messages: chancreate, send, recv, nbsend and nbrecv.</summary>
public sealed class Channel<T> : Channel
{
    public Channel(int size = 0)
        : base(size) => Buffer = new T[size];

    internal T[] Buffer { get; }

    /// <summary>send: 1 once the message is received or buffered, -1 if interrupted or the channel is closed.</summary>
    public async Task<int> SendAsync(T value, CancellationToken interrupt = default)
    {
        var entry = Alt<T>.Send(this, value);
        return Outcome(await Alt.AltAsync([entry], interrupt), entry);
    }

    /// <summary>recv: 1 and the message, or -1 if interrupted or the channel is closed and empty.</summary>
    public async Task<(int Result, T Value)> RecvAsync(CancellationToken interrupt = default)
    {
        var entry = Alt<T>.Recv(this);
        int result = Outcome(await Alt.AltAsync([entry], interrupt), entry);
        return (result, entry.Value!);
    }

    /// <summary>nbsend: as send, but 0 rather than blocking.</summary>
    public async Task<int> NbSendAsync(T value)
    {
        var entry = Alt<T>.Send(this, value);
        return Outcome(await Alt.NbAltAsync([entry]), entry);
    }

    /// <summary>nbrecv: as recv, but 0 rather than blocking.</summary>
    public async Task<(int Result, T Value)> NbRecvAsync()
    {
        var entry = Alt<T>.Recv(this);
        int result = Outcome(await Alt.NbAltAsync([entry]), entry);
        return (result, entry.Value!);
    }

    // channel.c's runop: alt on one entry, then -1 interrupted, 0 would block, 1 done unless done by a close.
    private static int Outcome(int chosen, Alt entry) => chosen switch
    {
        -1 => -1,
        0 => entry.Error is null ? 1 : -1,
        _ => 0,
    };
}
