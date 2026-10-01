using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Protocol;
using NinePSharp.Server;

namespace NinePSharp.Fog.Server;

public sealed record FogNinePLimits(int Sessions, int FidsPerSession, int RequestsPerSession,
    uint MessageSize, long SnapshotBytesPerSession, TimeSpan SnapshotLifetime, TimeSpan SessionLifetime);

/// <summary>Standard 9P2000 control export, for already mutually authenticated enrolled-node TLS transports.</summary>
public sealed class FogNinePDispatcher : INinePFSDispatcher, INinePSessionLifecycle
{
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly FogFileTree tree;
    private readonly FogNodePolicy policy;
    private readonly FogNinePLimits limits;
    private readonly TimeProvider time;

    public FogNinePDispatcher(FogFileTree tree, FogNodePolicy policy, FogNinePLimits limits, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Sessions <= 0 || limits.FidsPerSession <= 0 || limits.RequestsPerSession <= 0 ||
            limits.MessageSize < 256 || limits.MessageSize > int.MaxValue || limits.SnapshotBytesPerSession <= 0 ||
            limits.SnapshotLifetime <= TimeSpan.Zero || limits.SessionLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
        this.tree = tree;
        this.policy = policy;
        this.limits = limits;
        this.time = time ?? TimeProvider.System;
    }

    public async Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(message);
        ISerializable? request = Payload(message);
        if (request is null) return new Rerror(NinePConstants.NoTag, "invalid-request");
        try
        {
            if (message is NinePMessage.MsgTversion version) return await VersionAsync(sessionId, version.Item);
            Session session;
            lock (gate)
            {
                if (!sessions.TryGetValue(sessionId, out session!)) throw new FogException("not-ready");
            }

            Pending pending;
            lock (session.Gate)
            {
                CheckSession(session);
                if (request.Size > session.MSize || request.Tag == NinePConstants.NoTag) throw new FogException("invalid-request");
                if (session.Pending.ContainsKey(request.Tag)) throw new FogException("busy");
                // One reserved flush slot lets a saturated client cancel, without unlimited waiters.
                if (message is NinePMessage.MsgTflush ? session.Flushes != 0 :
                    session.Pending.Count - session.Flushes >= limits.RequestsPerSession) throw new FogException("busy");
                pending = new Pending();
                session.Pending[request.Tag] = pending;
                if (message is NinePMessage.MsgTflush) session.Flushes++;
            }

            try
            {
                Task<object> operation;
                lock (session.Gate) operation = message is NinePMessage.MsgTflush flush ?
                    FlushAsync(session, flush.Item, pending.Cancellation.Token) :
                    DispatchCore(sessionId, session, message, certificate, pending.Cancellation.Token);
                object response = await operation;
                if (response is ISerializable serializable && serializable.Size > session.MSize) throw new FogException("limit");
                return response;
            }
            finally
            {
                lock (session.Gate)
                {
                    session.Pending.Remove(request.Tag);
                    if (message is NinePMessage.MsgTflush) session.Flushes--;
                    pending.Completion.TrySetResult();
                    pending.Cancellation.Dispose();
                }
            }
        }
        catch (FogException exception) { return new Rerror(request.Tag, exception.Code); }
        catch (OperationCanceledException) { return new Rerror(request.Tag, "interrupted"); }
        catch (Exception) { return new Rerror(request.Tag, "unavailable"); }
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        Session? session;
        lock (gate)
        {
            if (!sessions.Remove(sessionId, out session)) return;
        }
        await ResetAsync(sessionId, session);
    }

    private async Task<object> VersionAsync(string id, Tversion request)
    {
        Session session;
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out session!))
            {
                if (sessions.Count >= limits.Sessions) throw new FogException("limit");
                session = new Session(time.GetTimestamp());
                sessions.Add(id, session);
            }
        }
        await ResetAsync(id, session);
        lock (session.Gate)
        {
            lock (gate)
            {
                Session? current = sessions.GetValueOrDefault(id);
                if (!ReferenceEquals(current, session))
                    throw new FogException("not-ready");
            }
            if (request.MSize < 256) throw new FogException("invalid-request");
            if (time.GetElapsedTime(session.Created) >= limits.SessionLifetime) throw new FogException("denied");
            session.MSize = Math.Min(request.MSize, limits.MessageSize);
            string version = request.Version.StartsWith("9P2000", StringComparison.Ordinal) ? "9P2000" : "unknown";
            session.Ready = version != "unknown";
            return new Rversion(request.Tag, session.MSize, version);
        }
    }

    private async Task ResetAsync(string id, Session session)
    {
        Pending[] pending;
        lock (session.Gate)
        {
            session.Ready = false;
            pending = session.Pending.Values.ToArray();
            foreach (var operation in pending) operation.Cancellation.Cancel();
        }
        await Task.WhenAll(pending.Select(operation => operation.Completion.Task));
        lock (session.Gate)
        {
            foreach (var fid in session.Fids.Values) fid.Open?.Dispose();
            session.Fids.Clear();
            session.SnapshotBytes = 0;
            tree.CloseSession(id);
        }
    }

    private static async Task<object> FlushAsync(Session session, Tflush request, CancellationToken cancellation)
    {
        Task completion = Task.CompletedTask;
        lock (session.Gate)
        {
            if (request.Tag == request.OldTag) throw new FogException("invalid-request");
            if (session.Pending.TryGetValue(request.OldTag, out var operation))
            {
                operation.Cancellation.Cancel();
                completion = operation.Completion.Task;
            }
        }
        await completion.WaitAsync(cancellation);
        return new Rflush(request.Tag);
    }

    private Task<object> DispatchCore(string id, Session session, NinePMessage message, X509Certificate2? certificate, CancellationToken cancellation)
    {
        object result = message switch
        {
            NinePMessage.MsgTattach attach => Attach(session, attach.Item, certificate),
            NinePMessage.MsgTwalk walk => Walk(session, walk.Item, certificate),
            NinePMessage.MsgTopen open => Open(id, session, open.Item, certificate),
            NinePMessage.MsgTread read => Read(session, read.Item, certificate),
            NinePMessage.MsgTclunk clunk => Clunk(session, clunk.Item, certificate),
            NinePMessage.MsgTstat stat => Stat(session, stat.Item, certificate),
            NinePMessage.MsgTwrite write => WriteAsync(session, write.Item, certificate, cancellation),
            _ => throw new FogException("denied"),
        };
        return result is Task<object> asynchronous ? asynchronous : Task.FromResult(result);
    }

    private Rattach Attach(Session session, Tattach request, X509Certificate2? certificate)
    {
        if (request.Afid != NinePConstants.NoFid || request.Aname != "runtime" || request.Fid == NinePConstants.NoFid) throw new FogException("denied");
        ReserveFid(session, request.Fid);
        FogPrincipal principal = policy.Attach(request.Uname, certificate);
        session.Fids.Add(request.Fid, new Fid(tree.Root, principal));
        return new Rattach(request.Tag, Qid(tree.Root));
    }

    private Rwalk Walk(Session session, Twalk request, X509Certificate2? certificate)
    {
        Fid source = GetFid(session, request.Fid, certificate);
        if (source.Open is not null || request.Wname.Length > 16) throw new FogException("invalid-request");
        if (request.NewFid != request.Fid) ReserveFid(session, request.NewFid);
        var node = source.Node;
        var qids = new List<Qid>();
        foreach (string name in request.Wname)
        {
            try
            {
                node = tree.Walk(source.Principal, node, name);
                qids.Add(Qid(node));
            }
            catch (FogException) when (qids.Count != 0) { break; }
        }
        // A partial walk returns its qids but does not establish newfid (9P walk(5)).
        if (qids.Count == request.Wname.Length) session.Fids[request.NewFid] = new Fid(node, source.Principal);
        return new Rwalk(request.Tag, qids.ToArray());
    }

    private Ropen Open(string id, Session session, Topen request, X509Certificate2? certificate)
    {
        Fid fid = GetFid(session, request.Fid, certificate);
        if (fid.Open is not null) throw new FogException("busy");
        PruneSnapshots(session);
        FogOpenFile opened;
        if (fid.Node.Directory)
        {
            if (request.Mode != NinePConstants.OREAD) throw new FogException("denied");
            using var stream = new MemoryStream();
            long remaining = limits.SnapshotBytesPerSession - session.SnapshotBytes;
            foreach (var node in tree.List(fid.Principal, fid.Node))
            {
                var stat = MakeStat(node);
                if (stat.Size > remaining) throw new FogException("snapshot-limit");
                byte[] bytes = new byte[stat.Size];
                int offset = 0;
                stat.WriteTo(bytes, ref offset);
                stream.Write(bytes);
                remaining -= stat.Size;
            }
            opened = new FogOpenFile(stream.ToArray());
        }
        else
        {
            opened = tree.Open(fid.Principal, id, fid.Node, request.Mode, limits.SnapshotBytesPerSession - session.SnapshotBytes);
            if ((opened.Snapshot?.LongLength ?? 0) > limits.SnapshotBytesPerSession - session.SnapshotBytes)
            {
                opened.Dispose();
                throw new FogException("snapshot-limit");
            }
        }
        long size = opened.Snapshot?.LongLength ?? 0;
        session.SnapshotBytes += size;
        fid.Open = opened;
        fid.Opened = time.GetTimestamp();
        return new Ropen(request.Tag, Qid(fid.Node), session.MSize - 24);
    }

    private Rread Read(Session session, Tread request, X509Certificate2? certificate)
    {
        Fid fid = GetFid(session, request.Fid, certificate);
        byte[] bytes = fid.Open?.Snapshot ?? throw new FogException("denied");
        if (time.GetElapsedTime(fid.Opened) >= limits.SnapshotLifetime)
        {
            DropSnapshot(session, fid);
            throw new FogException("tx-expired");
        }
        uint count = Math.Min(request.Count, session.MSize - 11);
        int offset = (int)Math.Min(request.Offset, (ulong)bytes.Length);
        int length = (int)Math.Min(count, (ulong)(bytes.Length - offset));
        if (fid.Node.Directory) length = DirectoryLength(bytes, (ulong)offset, length);
        return new Rread(request.Tag, bytes.AsMemory(offset, length));
    }

    private async Task<object> WriteAsync(Session session, Twrite request, X509Certificate2? certificate, CancellationToken cancellation)
    {
        Fid fid = GetFid(session, request.Fid, certificate);
        var write = fid.Open?.Write ?? throw new FogException("denied");
        if (fid.Writing) throw new FogException("busy");
        fid.Writing = true;
        try
        {
            uint count = await write(request.Offset, request.Data, cancellation);
            lock (session.Gate) policy.Check(fid.Principal, certificate);
            return new Rwrite(request.Tag, count);
        }
        finally { lock (session.Gate) fid.Writing = false; }
    }

    private Rclunk Clunk(Session session, Tclunk request, X509Certificate2? certificate)
    {
        if (!session.Fids.TryGetValue(request.Fid, out var fid)) throw new FogException("invalid-request");
        if (fid.Writing) throw new FogException("busy");
        session.Fids.Remove(request.Fid);
        try
        {
            policy.Check(fid.Principal, certificate);
            fid.Open?.Clunk();
        }
        finally
        {
            session.SnapshotBytes -= fid.Open?.Snapshot?.LongLength ?? 0;
            fid.Open?.Dispose();
        }
        return new Rclunk(request.Tag);
    }

    private Rstat Stat(Session session, Tstat request, X509Certificate2? certificate)
    {
        Fid fid = GetFid(session, request.Fid, certificate);
        return new Rstat(request.Tag, MakeStat(fid.Node, (ulong)(fid.Open?.Snapshot?.Length ?? 0)));
    }

    private Fid GetFid(Session session, uint number, X509Certificate2? certificate)
    {
        if (!session.Fids.TryGetValue(number, out var fid)) throw new FogException("invalid-request");
        policy.Check(fid.Principal, certificate);
        tree.Check(fid.Principal, fid.Node);
        return fid;
    }

    private void ReserveFid(Session session, uint number)
    {
        if (number == NinePConstants.NoFid || session.Fids.ContainsKey(number)) throw new FogException("busy");
        if (session.Fids.Count >= limits.FidsPerSession) throw new FogException("limit");
    }

    private void CheckSession(Session session)
    {
        if (!session.Ready) throw new FogException("not-ready");
        if (time.GetElapsedTime(session.Created) >= limits.SessionLifetime) throw new FogException("denied");
    }

    private void PruneSnapshots(Session session)
    {
        foreach (var fid in session.Fids.Values)
            if (fid.Open?.Snapshot is not null && time.GetElapsedTime(fid.Opened) >= limits.SnapshotLifetime) DropSnapshot(session, fid);
    }

    private static void DropSnapshot(Session session, Fid fid)
    {
        session.SnapshotBytes -= fid.Open!.Snapshot!.Length;
        fid.Open.Dispose();
        // An expired open remains opened; it cannot be used to allocate another clone or snapshot.
        fid.Open = new FogOpenFile();
    }

    private static int DirectoryLength(byte[] bytes, ulong offset, int maximum)
    {
        int cursor = 0;
        while (cursor < (long)offset) cursor += System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor)) + 2;
        if (cursor != (long)offset) throw new FogException("invalid-request");
        int start = cursor;
        while (cursor < bytes.Length)
        {
            int size = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor)) + 2;
            if (size > maximum - (cursor - start)) break;
            cursor += size;
        }
        return cursor - start;
    }

    private static Qid Qid(FogFileNode node) => new(node.Directory ? QidType.QTDIR : QidType.QTFILE, 0, node.QidPath);
    private static Stat MakeStat(FogFileNode node, ulong length = 0) => new(0, 0, 0, Qid(node),
        node.Directory ? 0x80000000U | 0x140U : 0x180U, 0, 0, length, node.Name, "fog", "fog", "fog", NinePDialect.NineP2000);

    private static ISerializable? Payload(NinePMessage message) => message switch
    {
        NinePMessage.MsgTversion m => m.Item, NinePMessage.MsgTauth m => m.Item, NinePMessage.MsgTattach m => m.Item,
        NinePMessage.MsgTflush m => m.Item, NinePMessage.MsgTwalk m => m.Item, NinePMessage.MsgTopen m => m.Item,
        NinePMessage.MsgTcreate m => m.Item, NinePMessage.MsgTread m => m.Item, NinePMessage.MsgTwrite m => m.Item,
        NinePMessage.MsgTclunk m => m.Item, NinePMessage.MsgTremove m => m.Item, NinePMessage.MsgTstat m => m.Item,
        NinePMessage.MsgTwstat m => m.Item, _ => null,
    };

    private sealed class Session(long created)
    {
        internal readonly object Gate = new();
        internal readonly long Created = created;
        internal readonly Dictionary<uint, Fid> Fids = new();
        internal readonly Dictionary<ushort, Pending> Pending = new();
        internal uint MSize;
        internal long SnapshotBytes;
        internal int Flushes;
        internal bool Ready;
    }
    private sealed class Fid(FogFileNode node, FogPrincipal principal)
    {
        internal readonly FogFileNode Node = node;
        internal readonly FogPrincipal Principal = principal;
        internal FogOpenFile? Open;
        internal long Opened;
        internal bool Writing;
    }
    private sealed class Pending
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
