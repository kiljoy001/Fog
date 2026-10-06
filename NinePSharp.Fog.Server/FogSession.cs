using System.Buffers.Binary;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Protocol;

namespace NinePSharp.Fog.Server;

/// <summary>
/// One 9P session as a process: a single loop owns its fids, pending requests, flushes and drains, and
/// everything else reaches it as an event in its inbox. Writes run outside the loop and report back.
/// </summary>
internal sealed class FogSession
{
    private readonly Channel<Event> inbox = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<uint, Fid> fids = new();
    private readonly Dictionary<ushort, Operation> pending = new();
    private readonly string id;
    private readonly FogFileTree tree;
    private readonly FogNodePolicy policy;
    private readonly FogNinePLimits limits;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly long created;
    private uint messageSize;
    private long snapshotBytes;
    private bool ready;
    private bool closed;

    internal FogSession(string id, FogFileTree tree, FogNodePolicy policy, FogNinePLimits limits, TimeProvider time, ILogger logger)
    {
        this.id = id;
        this.tree = tree;
        this.policy = policy;
        this.limits = limits;
        this.time = time;
        this.logger = logger;
        created = time.GetTimestamp();
        _ = RunAsync();
    }

    internal Task<object> SendAsync(NinePMessage message, ISerializable request, X509Certificate2? certificate)
    {
        var reply = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inbox.Writer.TryWrite(new Request(message, request, certificate, reply)))
        {
            reply.SetResult(new Rerror(request.Tag, "not-ready"));
        }

        return reply.Task;
    }

    internal Task CloseAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inbox.Writer.TryWrite(new Close(done)))
        {
            done.SetResult();
        }

        return done.Task;
    }

    private static Rerror Error(ushort tag, Exception exception) => exception switch
    {
        FogException fog => new Rerror(tag, fog.Code),
        OperationCanceledException => new Rerror(tag, "interrupted"),
        _ => new Rerror(tag, "unavailable"),
    };

    private static Qid Qid(FogFileNode node) => new(node.Directory ? QidType.QTDIR : QidType.QTFILE, 0, node.QidPath);

    private static Stat MakeStat(FogFileNode node, ulong length = 0) => new(
        0,
        0,
        0,
        Qid(node),
        node.Directory ? 0x80000000U | 0x140U : 0x180U,
        0,
        0,
        length,
        node.Name,
        "fog",
        "fog",
        "fog",
        NinePDialect.NineP2000);

    private static int DirectoryLength(byte[] bytes, ulong offset, int maximum)
    {
        int cursor = 0;
        while (cursor < (long)offset)
        {
            cursor += BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor)) + 2;
        }

        if (cursor != (long)offset)
        {
            throw new FogException("invalid-request");
        }

        int start = cursor;
        while (cursor < bytes.Length)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor)) + 2;
            if (size > maximum - (cursor - start))
            {
                break;
            }

            cursor += size;
        }

        return cursor - start;
    }

    private async Task RunAsync()
    {
        await foreach (Event next in inbox.Reader.ReadAllAsync())
        {
            try
            {
                Handle(next);
            }
            catch (Exception exception)
            {
                // The loop must outlive any one event, or every later request would wait forever.
                if (next is Request request)
                {
                    request.Reply.TrySetResult(Error(request.Payload.Tag, exception));
                }
                else
                {
                    logger.LogError(exception, "Session {Session} failed to handle {Event}.", id, next.GetType().Name);
                }
            }
        }
    }

    private void Handle(Event next)
    {
        switch (next)
        {
            case Request request:
                Receive(request);
                break;
            case Finished finished:
                Finish(finished.Operation, finished.Result);
                break;
            case Expired expired:
                Expire(expired.Drain);
                break;
            case Close close:
                Shut(close.Done);
                break;
        }
    }

    private void Receive(Request request)
    {
        ushort tag = request.Payload.Tag;
        if (closed)
        {
            request.Reply.TrySetResult(new Rerror(tag, "not-ready"));
            return;
        }

        if (request.Message is NinePMessage.MsgTversion version)
        {
            Reset(() => request.Reply.TrySetResult(Negotiate(version.Item)));
            return;
        }

        try
        {
            Admit(request);
            if (request.Message is NinePMessage.MsgTflush flush)
            {
                Flush(flush.Item, request.Reply);
            }
            else if (request.Message is NinePMessage.MsgTwrite write)
            {
                StartWrite(write.Item, request);
            }
            else
            {
                request.Reply.TrySetResult(Bounded(Execute(request.Message, request.Certificate)));
            }
        }
        catch (Exception exception) when (exception is FogException)
        {
            request.Reply.TrySetResult(Error(tag, exception));
        }
    }

    private void Admit(Request request)
    {
        if (!ready)
        {
            throw new FogException("not-ready");
        }

        if (time.GetElapsedTime(created) >= limits.SessionLifetime)
        {
            throw new FogException("denied");
        }

        if (request.Payload.Size > messageSize || request.Payload.Tag == NinePConstants.NoTag)
        {
            throw new FogException("invalid-request");
        }

        if (pending.ContainsKey(request.Payload.Tag))
        {
            throw new FogException("busy");
        }

        // flush(5) never refuses a flush; at most one waits per request, so flushes stay bounded too.
        if (request.Message is not NinePMessage.MsgTflush && pending.Values.Count(operation => !operation.IsFlush) >= limits.RequestsPerSession)
        {
            throw new FogException("busy");
        }
    }

    private object Bounded(object response) =>
        response is ISerializable serializable && serializable.Size > messageSize ? throw new FogException("limit") : response;

    private object Execute(NinePMessage message, X509Certificate2? certificate) => message switch
    {
        NinePMessage.MsgTattach attach => Attach(attach.Item, certificate),
        NinePMessage.MsgTwalk walk => Walk(walk.Item, certificate),
        NinePMessage.MsgTopen open => Open(open.Item, certificate),
        NinePMessage.MsgTread read => Read(read.Item, certificate),
        NinePMessage.MsgTclunk clunk => Clunk(clunk.Item, certificate),
        NinePMessage.MsgTstat stat => Stat(stat.Item, certificate),
        _ => throw new FogException("denied"),
    };

    // flush(5): a flush is answered Rflush once its request is answered or abandoned, and at once when it
    // names its own tag, an unused tag or another flush. Of several flushes of one request only the last
    // needs an answer, so a newer flush finishes the older one, which is sent as nothing.
    private void Flush(Tflush request, TaskCompletionSource<object> reply)
    {
        if (request.OldTag == request.Tag || !pending.TryGetValue(request.OldTag, out var target) || target.IsFlush)
        {
            reply.TrySetResult(new Rflush(request.Tag));
            return;
        }

        var flush = new Operation(request.Tag, reply, isFlush: true);
        pending.Add(request.Tag, flush);
        target.Cancellation.Cancel();
        if (target.Flusher is { } older)
        {
            Answer(older, FogNinePDispatcher.NoReply);
        }

        target.Flusher = flush;
        if (!target.FlushDraining)
        {
            target.FlushDraining = true;
            StartDrain([target], () => Answer(target.Flusher!, new Rflush(target.Flusher!.Tag)));
        }
    }

    private void StartWrite(Twrite request, Request received)
    {
        Fid fid = GetFid(request.Fid, received.Certificate);
        var write = fid.Open?.Write ?? throw new FogException("denied");
        if (fid.Writing)
        {
            throw new FogException("busy");
        }

        var operation = new Operation(request.Tag, received.Reply, isFlush: false) { Fid = fid, Certificate = received.Certificate };
        pending.Add(request.Tag, operation);
        fid.Writing = true;
        _ = WriteAsync(operation, write, request);
    }

    private async Task WriteAsync(Operation operation, Func<ulong, ReadOnlyMemory<byte>, CancellationToken, Task<uint>> write, Twrite request)
    {
        object result;
        try
        {
            result = await write(request.Offset, request.Data, operation.Cancellation.Token);
        }
        catch (Exception exception)
        {
            result = exception;
        }

        inbox.Writer.TryWrite(new Finished(operation, result));
    }

    private void Finish(Operation operation, object result)
    {
        operation.Fid!.Writing = false;
        if (operation.Answered)
        {
            return;
        }

        object response;
        try
        {
            response = result is uint count ? Bounded(Written(operation, count)) : Error(operation.Tag, (Exception)result);
        }
        catch (FogException exception)
        {
            response = Error(operation.Tag, exception);
        }

        Answer(operation, response);
    }

    private Rwrite Written(Operation operation, uint count)
    {
        policy.Check(operation.Fid!.Principal, operation.Certificate);
        return new Rwrite(operation.Tag, count);
    }

    // Replies, frees the tag, and lets every drain waiting for this request move on.
    private void Answer(Operation operation, object response)
    {
        if (operation.Answered)
        {
            return;
        }

        operation.Answered = true;
        pending.Remove(operation.Tag);
        operation.Cancellation.Dispose();
        operation.Reply.TrySetResult(response);
        foreach (var drain in operation.Drains)
        {
            drain.Waiting.Remove(operation);
            if (drain.Waiting.Count == 0)
            {
                drain.Done();
            }
        }
    }

    // Waits at most the drain limit for the operations, then abandons those still running.
    private void StartDrain(IEnumerable<Operation> operations, Action done)
    {
        var drain = new Drain(done);
        foreach (var operation in operations.Where(operation => !operation.Answered))
        {
            drain.Waiting.Add(operation);
            operation.Drains.Add(drain);
        }

        if (drain.Waiting.Count == 0)
        {
            done();
            return;
        }

        _ = ExpireAsync(drain);
    }

    private async Task ExpireAsync(Drain drain)
    {
        await Task.Delay(limits.Drain);
        inbox.Writer.TryWrite(new Expired(drain));
    }

    private void Expire(Drain drain)
    {
        // Requests first, so a flush is never answered while the request it flushes still holds its tag.
        foreach (var operation in drain.Waiting.OrderBy(operation => operation.IsFlush).ToArray())
        {
            // An abandoned flush has no effect to be unsure of, and flush(5) allows it only an Rflush.
            if (operation.IsFlush)
            {
                Answer(operation, new Rflush(operation.Tag));
                continue;
            }

            logger.LogWarning("Request {Tag} was abandoned after the drain limit; its outcome is unknown.", operation.Tag);
            Answer(operation, new Rerror(operation.Tag, "unknown"));
        }
    }

    // Tversion and closing both interrupt every request, drain them, and free every fid.
    private void Reset(Action then)
    {
        ready = false;
        Operation[] operations = pending.Values.ToArray();
        foreach (var operation in operations)
        {
            operation.Cancellation.Cancel();
        }

        StartDrain(operations, () =>
        {
            foreach (var fid in fids.Values)
            {
                fid.Open?.Dispose();
            }

            fids.Clear();
            snapshotBytes = 0;
            tree.CloseSession(id);
            then();
        });
    }

    private object Negotiate(Tversion request)
    {
        if (closed)
        {
            return new Rerror(request.Tag, "not-ready");
        }

        if (request.MSize < 256)
        {
            return new Rerror(request.Tag, "invalid-request");
        }

        if (time.GetElapsedTime(created) >= limits.SessionLifetime)
        {
            return new Rerror(request.Tag, "denied");
        }

        messageSize = Math.Min(request.MSize, limits.MessageSize);
        string version = request.Version.StartsWith("9P2000", StringComparison.Ordinal) ? "9P2000" : "unknown";
        ready = version != "unknown";
        return new Rversion(request.Tag, messageSize, version);
    }

    private void Shut(TaskCompletionSource done)
    {
        closed = true;
        Reset(() =>
        {
            inbox.Writer.TryComplete();
            done.TrySetResult();
        });
    }

    private Rattach Attach(Tattach request, X509Certificate2? certificate)
    {
        if (request.Afid != NinePConstants.NoFid || request.Aname != "runtime" || request.Fid == NinePConstants.NoFid)
        {
            throw new FogException("denied");
        }

        ReserveFid(request.Fid);
        FogPrincipal principal = policy.Attach(request.Uname, certificate);
        fids.Add(request.Fid, new Fid(tree.Root, principal));
        return new Rattach(request.Tag, Qid(tree.Root));
    }

    private Rwalk Walk(Twalk request, X509Certificate2? certificate)
    {
        Fid source = GetFid(request.Fid, certificate);
        if (source.Open is not null || request.Wname.Length > 16)
        {
            throw new FogException("invalid-request");
        }

        if (request.NewFid != request.Fid)
        {
            ReserveFid(request.NewFid);
        }

        var node = source.Node;
        var qids = new List<Qid>();
        foreach (string name in request.Wname)
        {
            try
            {
                node = tree.Walk(source.Principal, node, name);
                qids.Add(Qid(node));
            }
            catch (FogException) when (qids.Count != 0)
            {
                break;
            }
        }

        // A partial walk returns its qids but does not establish newfid (9P walk(5)).
        if (qids.Count == request.Wname.Length)
        {
            fids[request.NewFid] = new Fid(node, source.Principal);
        }

        return new Rwalk(request.Tag, qids.ToArray());
    }

    private Ropen Open(Topen request, X509Certificate2? certificate)
    {
        Fid fid = GetFid(request.Fid, certificate);
        if (fid.Open is not null)
        {
            throw new FogException("busy");
        }

        PruneSnapshots();
        FogOpenFile opened = fid.Node.Directory ? OpenDirectory(fid, request.Mode) : OpenFile(fid, request.Mode);
        snapshotBytes += opened.Snapshot?.LongLength ?? 0;
        fid.Open = opened;
        fid.Opened = time.GetTimestamp();
        return new Ropen(request.Tag, Qid(fid.Node), messageSize - 24);
    }

    private FogOpenFile OpenDirectory(Fid fid, byte mode)
    {
        if (mode != NinePConstants.OREAD)
        {
            throw new FogException("denied");
        }

        using var stream = new MemoryStream();
        long remaining = limits.SnapshotBytesPerSession - snapshotBytes;
        foreach (var node in tree.List(fid.Principal, fid.Node))
        {
            var stat = MakeStat(node);
            if (stat.Size > remaining)
            {
                throw new FogException("snapshot-limit");
            }

            byte[] bytes = new byte[stat.Size];
            int offset = 0;
            stat.WriteTo(bytes, ref offset);
            stream.Write(bytes);
            remaining -= stat.Size;
        }

        return new FogOpenFile(stream.ToArray());
    }

    private FogOpenFile OpenFile(Fid fid, byte mode)
    {
        FogOpenFile opened = tree.Open(fid.Principal, id, fid.Node, mode, limits.SnapshotBytesPerSession - snapshotBytes);
        if ((opened.Snapshot?.LongLength ?? 0) > limits.SnapshotBytesPerSession - snapshotBytes)
        {
            opened.Dispose();
            throw new FogException("snapshot-limit");
        }

        return opened;
    }

    private Rread Read(Tread request, X509Certificate2? certificate)
    {
        Fid fid = GetFid(request.Fid, certificate);
        byte[] bytes = fid.Open?.Snapshot ?? throw new FogException("denied");
        if (time.GetElapsedTime(fid.Opened) >= limits.SnapshotLifetime)
        {
            DropSnapshot(fid);
            throw new FogException("tx-expired");
        }

        uint count = Math.Min(request.Count, messageSize - 11);
        int offset = (int)Math.Min(request.Offset, (ulong)bytes.Length);
        int length = (int)Math.Min(count, (ulong)(bytes.Length - offset));
        if (fid.Node.Directory)
        {
            length = DirectoryLength(bytes, (ulong)offset, length);
        }

        return new Rread(request.Tag, bytes.AsMemory(offset, length));
    }

    private Rclunk Clunk(Tclunk request, X509Certificate2? certificate)
    {
        if (!fids.TryGetValue(request.Fid, out var fid))
        {
            throw new FogException("invalid-request");
        }

        if (fid.Writing)
        {
            throw new FogException("busy");
        }

        fids.Remove(request.Fid);
        try
        {
            policy.Check(fid.Principal, certificate);
            fid.Open?.Clunk();
        }
        finally
        {
            snapshotBytes -= fid.Open?.Snapshot?.LongLength ?? 0;
            fid.Open?.Dispose();
        }

        return new Rclunk(request.Tag);
    }

    private Rstat Stat(Tstat request, X509Certificate2? certificate)
    {
        Fid fid = GetFid(request.Fid, certificate);
        return new Rstat(request.Tag, MakeStat(fid.Node, (ulong)(fid.Open?.Snapshot?.Length ?? 0)));
    }

    private Fid GetFid(uint number, X509Certificate2? certificate)
    {
        if (!fids.TryGetValue(number, out var fid))
        {
            throw new FogException("invalid-request");
        }

        policy.Check(fid.Principal, certificate);
        tree.Check(fid.Principal, fid.Node);
        return fid;
    }

    private void ReserveFid(uint number)
    {
        if (number == NinePConstants.NoFid || fids.ContainsKey(number))
        {
            throw new FogException("busy");
        }

        if (fids.Count >= limits.FidsPerSession)
        {
            throw new FogException("limit");
        }
    }

    private void PruneSnapshots()
    {
        foreach (var fid in fids.Values)
        {
            if (fid.Open?.Snapshot is not null && time.GetElapsedTime(fid.Opened) >= limits.SnapshotLifetime)
            {
                DropSnapshot(fid);
            }
        }
    }

    private void DropSnapshot(Fid fid)
    {
        snapshotBytes -= fid.Open!.Snapshot!.Length;
        fid.Open.Dispose();

        // An expired open remains opened; it cannot be used to allocate another clone or snapshot.
        fid.Open = new FogOpenFile();
    }

    private abstract record Event;

    private sealed record Request(NinePMessage Message, ISerializable Payload, X509Certificate2? Certificate, TaskCompletionSource<object> Reply) : Event;

    private sealed record Finished(Operation Operation, object Result) : Event;

    private sealed record Expired(Drain Drain) : Event;

    private sealed record Close(TaskCompletionSource Done) : Event;

    private sealed class Fid(FogFileNode node, FogPrincipal principal)
    {
        internal FogFileNode Node { get; } = node;

        internal FogPrincipal Principal { get; } = principal;

        internal FogOpenFile? Open { get; set; }

        internal long Opened { get; set; }

        internal bool Writing { get; set; }
    }

    // A request still waiting for its answer: a write running outside the loop, or a flush.
    private sealed class Operation(ushort tag, TaskCompletionSource<object> reply, bool isFlush)
    {
        internal ushort Tag { get; } = tag;

        internal TaskCompletionSource<object> Reply { get; } = reply;

        internal bool IsFlush { get; } = isFlush;

        internal CancellationTokenSource Cancellation { get; } = new();

        internal List<Drain> Drains { get; } = new();

        internal Fid? Fid { get; init; }

        internal X509Certificate2? Certificate { get; init; }

        internal Operation? Flusher { get; set; }

        internal bool FlushDraining { get; set; }

        internal bool Answered { get; set; }
    }

    private sealed class Drain(Action done)
    {
        internal HashSet<Operation> Waiting { get; } = new();

        internal Action Done { get; } = done;
    }
}
