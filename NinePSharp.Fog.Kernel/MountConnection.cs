using System.Buffers.Binary;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devmnt's Mnt: one connection to a file server, shared by every mount on it. Each request has a tag
// of its own; one caller at a time reads replies and hands each to the request it answers, and when
// its own has come, lets the next waiting caller read, as mountio, mountmux and mntgate do.
internal sealed class MountConnection
{
    public const string RpcError = "mount rpc error";
    public const int HeaderRoom = 24;
    public const int MaxRpc = HeaderRoom + 32768;
    private const ushort NoTag = 0xFFFF;

    private readonly object gate = new();
    private readonly IResourceDataOperations device;
    private readonly ResourceOpenHandle transport;
    private readonly IAsyncDisposable lease;
    private readonly Func<ResourceOperationContext> context;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly List<Rpc> queue = [];
    private readonly HashSet<ushort> tags = [];
    private byte[] input = [];
    private Rpc? rip;
    private Task<byte[]>? reading;
    private bool broken;
    private ushort nextTag;
    private uint nextFid;

    // The connection holds the transport's lease, and lets it go when the server hangs up.
    public MountConnection(IResourceDataOperations device, ResourceOpenHandle transport, IAsyncDisposable lease, Func<ResourceOperationContext> context, uint firstFid = 0)
    {
        nextFid = firstFid;
        this.device = device;
        this.transport = transport;
        this.lease = lease;
        this.context = context;
    }

    public int MessageSize { get; private set; }

    public uint NewFid()
    {
        lock (gate)
        {
            if (nextFid == NinePConstants.NoFid)
            {
                nextFid++;
            }

            return nextFid++;
        }
    }

    // mntversion: not multiplexed, sent once before anything else on the connection.
    public async Task VersionAsync()
    {
        await WriteAsync(Encode(new Tversion(NoTag, MaxRpc, "9P2000")));
        byte[] reply = await ReadFrameAsync();
        if ((MessageTypes)reply[4] != MessageTypes.Rversion)
        {
            throw new IOException((MessageTypes)reply[4] == MessageTypes.Rerror ? new Rerror(reply).Ename : "unexpected reply type in fversion");
        }

        var version = new Rversion(reply);
        if (version.MSize > MaxRpc)
        {
            throw new IOException("server tries to increase msize in fversion");
        }

        // devmnt also refuses one over a megabyte, which MaxRpc's bound has refused already.
        if (version.MSize < 256)
        {
            throw new IOException("nonsense value of msize in fversion");
        }

        if (!version.Version.StartsWith("9P2000", StringComparison.Ordinal))
        {
            throw new IOException("bad 9P version returned from server");
        }

        MessageSize = (int)version.MSize;
    }

    // mountrpc: the reply of the request's own type, or the server's error as the call's.
    public async Task<byte[]> RpcAsync(Func<ushort, ISerializable> request, MessageTypes expected, CancellationToken cancellationToken)
    {
        Rpc r = Allocate();
        try
        {
            byte[] reply = await IoAsync(r, Encode(request(r.Tag)), cancellationToken);
            var type = (MessageTypes)reply[4];
            if (type == MessageTypes.Rerror)
            {
                throw new MountErrorException(new Rerror(reply).Ename);
            }

            if (type != expected)
            {
                throw new IOException(RpcError);
            }

            return reply;
        }
        finally
        {
            Free(r);
        }
    }

    // The task's end or the token's cancellation, whichever comes first; the task itself goes on. Both
    // set one signal, as Rendezvous does: a Coyote-rewritten caller controls neither WaitAsync nor a
    // Task.WhenAny of a signal's task. Unregistered rather than disposed, which would wait for a
    // cancellation already running. A task's failure is its waiter's to see when it awaits it.
    private static async Task UntilAsync(Task task, CancellationToken cancellationToken)
    {
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(() => ended.TrySetResult(false));
        _ = SignalAsync(task, ended);
        bool done = await ended.Task;

        // Stryker disable once Statement : unregistering only spares the token a callback that can no longer change the signal it would set
        registration.Unregister();
        if (!done)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static async Task SignalAsync(Task task, TaskCompletionSource<bool> ended)
    {
        try
        {
            await task;
        }
        catch
        {
            // Whoever waits on the task has its failure.
        }

        ended.TrySetResult(true);
    }

    private static byte[] Encode(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }

    // mountio: queue the request, send it, then wait to read, or for whoever reads to hand over the
    // reply. A caller cancelled while it waits flushes the request and has its answer only if the
    // reply came first; the reader's read itself goes on for the next caller who reads.
    private async Task<byte[]> IoAsync(Rpc r, byte[] message, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (broken)
            {
                throw new IOException(RpcError);
            }

            queue.Add(r);
        }

        try
        {
            await WriteAsync(message);
        }
        catch
        {
            Remove(r);
            throw;
        }

        try
        {
            await AwaitReplyAsync(r, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FlushAsync(r);
            if (r.Reply is null || (MessageTypes)r.Reply[4] == MessageTypes.Rerror)
            {
                throw;
            }
        }

        return r.Reply ?? throw new IOException(RpcError);
    }

    private async Task AwaitReplyAsync(Rpc r, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wake;
            lock (gate)
            {
                if (r.Done)
                {
                    return;
                }

                if (rip is null)
                {
                    rip = r;
                    break;
                }

                r.Wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = r.Wake.Task;
            }

            try
            {
                await UntilAsync(wake, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // No longer asleep: a reader's handoff it had been given passes to the next caller.
                lock (gate)
                {
                    r.Wake = null;
                    if (rip is null)
                    {
                        Wakeup();
                    }
                }

                throw;
            }
        }

        try
        {
            while (!r.Done)
            {
                Task<byte[]> frame;
                lock (gate)
                {
                    frame = reading ??= ReadFrameAsync();
                }

                byte[] reply;
                try
                {
                    await UntilAsync(frame, cancellationToken);
                    reply = await frame;
                }
                catch (IOException)
                {
                    Break();
                    throw new IOException(RpcError);
                }

                Mux(frame, reply);
            }
        }
        finally
        {
            Gate();
        }
    }

    // mountmux: hand the reply to the request it answers, waking its caller unless it is the reader.
    private void Mux(Task<byte[]> frame, byte[] reply)
    {
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5, 2));
        lock (gate)
        {
            if (reading == frame)
            {
                reading = null;
            }

            Rpc? answered = queue.Find(q => q.Tag == tag);
            if (answered is null)
            {
                return;
            }

            queue.Remove(answered);
            answered.Reply = reply;
            answered.Done = true;
            answered.Wake?.TrySetResult();
        }
    }

    // mntgate: the reader stands down and wakes the first caller still asleep.
    private void Gate()
    {
        lock (gate)
        {
            rip = null;
            Wakeup();
        }
    }

    // wakeup says whether anyone slept: a caller already woken, or no longer waiting, is passed over.
    private void Wakeup()
    {
        _ = queue.FirstOrDefault(q => !q.Done && q.Wake is { } wake && wake.TrySetResult());
    }

    // mntflushalloc and mntflushfree: a Tflush naming the request, waited for to its end; then the
    // request, if still unanswered, leaves the queue.
    private async Task FlushAsync(Rpc r)
    {
        Rpc flush = Allocate();
        try
        {
            await IoAsync(flush, Encode(new Tflush(flush.Tag, r.Tag)), CancellationToken.None);
        }
        catch (IOException)
        {
            // A connection that broke while flushing has failed the request too.
        }
        finally
        {
            Free(flush);
            Remove(r);
        }
    }

    // A connection whose reads fail is broken: every request still waiting fails with mount rpc
    // error, and so does every later one.
    private void Break()
    {
        lock (gate)
        {
            broken = true;
            reading = null;
            foreach (Rpc q in queue)
            {
                q.Done = true;
                q.Wake?.TrySetResult();
            }

            queue.Clear();
        }

        _ = lease.DisposeAsync().AsTask();
    }

    private void Remove(Rpc r)
    {
        lock (gate)
        {
            r.Done = true;
            queue.Remove(r);
        }
    }

    private Rpc Allocate()
    {
        lock (gate)
        {
            do
            {
                nextTag = (ushort)(nextTag + 1);
            }
            while (nextTag is 0 or NoTag || tags.Contains(nextTag));

            tags.Add(nextTag);
            return new Rpc(nextTag);
        }
    }

    private void Free(Rpc r)
    {
        lock (gate)
        {
            tags.Remove(r.Tag);
        }
    }

    // The transport's calls are awaited as Tasks, so a Coyote-rewritten caller controls each wait.
    private async Task WriteAsync(byte[] message)
    {
        await writing.WaitAsync();
        try
        {
            await device.WriteAsync(transport, 0, message, context(), CancellationToken.None).AsTask();
        }
        finally
        {
            writing.Release();
        }
    }

    // mntrpcread: a whole message, its length first; a short read past the end of the connection,
    // or a length out of bounds, fails it.
    private async Task<byte[]> ReadFrameAsync()
    {
        await FillAsync(4);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(input);
        if (length < NinePConstants.HeaderSize || length > Math.Max(MessageSize, MaxRpc))
        {
            throw new IOException(RpcError);
        }

        await FillAsync(length);
        byte[] frame = input[..length];
        input = input[length..];
        return frame;
    }

    private async Task FillAsync(int count)
    {
        while (input.Length < count)
        {
            ReadOnlyMemory<byte> more = await device.ReadAsync(transport, 0, (uint)Math.Max(MessageSize, MaxRpc), CancellationToken.None).AsTask();
            if (more.IsEmpty)
            {
                throw new IOException(RpcError);
            }

            input = [.. input, .. more.Span];
        }
    }

    private sealed class Rpc(ushort tag)
    {
        public ushort Tag { get; } = tag;

        public bool Done { get; set; }

        public byte[]? Reply { get; set; }

        public TaskCompletionSource? Wake { get; set; }
    }
}
