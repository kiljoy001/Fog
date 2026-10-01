using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;

namespace NinePSharp.Fog.Namespaces;

/// <summary>Bounds for one namespace export, with the same meanings as the Fog control export's.</summary>
public sealed record FogNamespaceLimits(int Sessions, int FidsPerSession, int RequestsPerSession, uint MessageSize, TimeSpan SessionLifetime);

/// <summary>
/// Applies the Fog export bounds around another 9P dispatcher: sessions, fids per session,
/// outstanding requests (with one reserved flush slot), negotiated message size and session
/// lifetime. Rejections use the control export's error names and never reach the inner dispatcher.
/// </summary>
public sealed class BoundedNamespaceExport : INinePFSDispatcher, INinePSessionLifecycle
{
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly INinePFSDispatcher inner;
    private readonly FogNamespaceLimits limits;
    private readonly TimeProvider time;

    /// <summary>Wraps a dispatcher with the given bounds.</summary>
    public BoundedNamespaceExport(INinePFSDispatcher inner, FogNamespaceLimits limits, TimeProvider? time = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Sessions <= 0 || limits.FidsPerSession <= 0 || limits.RequestsPerSession <= 0 ||
            limits.MessageSize < 256 || limits.MessageSize > int.MaxValue || limits.SessionLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
        this.limits = limits;
        this.time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
    {
        ISerializable? request = Payload(message);
        if (request is null) return new Rerror(NinePConstants.NoTag, "invalid-request");
        if (request is Tversion version) return await VersionAsync(sessionId, version, dialect, certificate);

        Session session;
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out session!) || session.MessageSize == 0) return new Rerror(request.Tag, "not-ready");
            if (Expired(session)) return new Rerror(request.Tag, "denied");
            if (request.Size > session.MessageSize || request.Tag == NinePConstants.NoTag) return new Rerror(request.Tag, "invalid-request");
            bool flush = request is Tflush;
            // One reserved flush slot lets a saturated client cancel, without unlimited waiters.
            if (flush ? session.Flushes != 0 : session.Pending - session.Flushes >= limits.RequestsPerSession) return new Rerror(request.Tag, "busy");
            if (NewFid(request) is uint fid && !session.Fids.Contains(fid) && session.Fids.Count >= limits.FidsPerSession)
                return new Rerror(request.Tag, "limit");
            session.Pending++;
            if (flush) session.Flushes++;
        }

        try
        {
            object response = await inner.DispatchAsync(sessionId, message, dialect, certificate);
            lock (gate) Account(session, request, response);
            return response;
        }
        finally
        {
            lock (gate)
            {
                session.Pending--;
                if (request is Tflush) session.Flushes--;
            }
        }
    }

    /// <inheritdoc/>
    public async Task CloseSessionAsync(string sessionId)
    {
        lock (gate)
        {
            if (!sessions.Remove(sessionId)) return;
        }

        if (inner is INinePSessionLifecycle lifecycle) await lifecycle.CloseSessionAsync(sessionId);
    }

    private async Task<object> VersionAsync(string sessionId, Tversion request, NinePDialect dialect, X509Certificate2? certificate)
    {
        Session session;
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out session!))
            {
                if (sessions.Count >= limits.Sessions) return new Rerror(request.Tag, "limit");
                session = new Session(time.GetTimestamp());
                sessions.Add(sessionId, session);
            }

            if (Expired(session)) return new Rerror(request.Tag, "denied");
            // version(5) aborts the session's outstanding state; its lifetime does not restart.
            session.Fids.Clear();
            session.MessageSize = 0;
        }

        var clamped = new Tversion(request.Tag, Math.Min(request.MSize, limits.MessageSize), request.Version);
        object response = await inner.DispatchAsync(sessionId, NinePMessage.NewMsgTversion(clamped), dialect, certificate);
        if (response is Rversion accepted)
            lock (gate) session.MessageSize = Math.Min(accepted.MSize, clamped.MSize);
        return response;
    }

    private bool Expired(Session session) => time.GetElapsedTime(session.Created) >= limits.SessionLifetime;

    private static uint? NewFid(ISerializable request) => request switch
    {
        Tattach attach => attach.Fid,
        Twalk walk when walk.NewFid != walk.Fid => walk.NewFid,
        _ => null,
    };

    private static void Account(Session session, ISerializable request, object response)
    {
        switch (request)
        {
            case Tattach attach when response is Rattach:
                session.Fids.Add(attach.Fid);
                break;
            // A partial walk does not establish newfid (walk(5)).
            case Twalk walk when response is Rwalk walked && walked.Wqid.Length == walk.Wname.Length:
                session.Fids.Add(walk.NewFid);
                break;
            case Tclunk clunk when response is Rclunk:
                session.Fids.Remove(clunk.Fid);
                break;
            // remove(5): the fid is clunked whether or not the remove succeeds.
            case Tremove remove:
                session.Fids.Remove(remove.Fid);
                break;
        }
    }

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
        internal long Created { get; } = created;
        internal HashSet<uint> Fids { get; } = new();
        internal uint MessageSize { get; set; }
        internal int Pending { get; set; }
        internal int Flushes { get; set; }
    }
}
