using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;

namespace NinePSharp.Fog.Server;

/// <summary>Standard 9P2000 control export, for already mutually authenticated enrolled-node TLS transports.</summary>
public sealed class FogNinePDispatcher : INinePFSDispatcher, INinePSessionLifecycle
{
    /// <summary>The result of a flush that a newer flush of the same request answers; it is sent as nothing.</summary>
    public static readonly object NoReply = new();

    private readonly object gate = new();
    private readonly Dictionary<string, FogSession> sessions = new(StringComparer.Ordinal);
    private readonly FogFileTree tree;
    private readonly FogNodePolicy policy;
    private readonly FogNinePLimits limits;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public FogNinePDispatcher(FogFileTree tree, FogNodePolicy policy, FogNinePLimits limits, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Sessions <= 0 || limits.FidsPerSession <= 0 || limits.RequestsPerSession <= 0 ||
            limits.MessageSize < 256 || limits.MessageSize > int.MaxValue || limits.SnapshotBytesPerSession <= 0 ||
            limits.SnapshotLifetime <= TimeSpan.Zero || limits.SessionLifetime <= TimeSpan.Zero || limits.Drain <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }

        this.tree = tree;
        this.policy = policy;
        this.limits = limits;
        this.time = time ?? TimeProvider.System;
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(message);
        ISerializable? request = Payload(message);
        if (request is null)
        {
            return new Rerror(NinePConstants.NoTag, "invalid-request");
        }

        FogSession? session;
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out session) && message is NinePMessage.MsgTversion)
            {
                if (sessions.Count >= limits.Sessions)
                {
                    return new Rerror(request.Tag, "limit");
                }

                session = new FogSession(sessionId, tree, policy, limits, time, logger);
                sessions.Add(sessionId, session);
            }
        }

        return session is null ? new Rerror(request.Tag, "not-ready") : await session.SendAsync(message, request, certificate);
    }

    public Task CloseSessionAsync(string sessionId)
    {
        FogSession? session;
        lock (gate)
        {
            if (!sessions.Remove(sessionId, out session))
            {
                return Task.CompletedTask;
            }
        }

        return session.CloseAsync();
    }

    private static ISerializable? Payload(NinePMessage message) => message switch
    {
        NinePMessage.MsgTversion m => m.Item, NinePMessage.MsgTauth m => m.Item, NinePMessage.MsgTattach m => m.Item,
        NinePMessage.MsgTflush m => m.Item, NinePMessage.MsgTwalk m => m.Item, NinePMessage.MsgTopen m => m.Item,
        NinePMessage.MsgTcreate m => m.Item, NinePMessage.MsgTread m => m.Item, NinePMessage.MsgTwrite m => m.Item,
        NinePMessage.MsgTclunk m => m.Item, NinePMessage.MsgTremove m => m.Item, NinePMessage.MsgTstat m => m.Item,
        NinePMessage.MsgTwstat m => m.Item, _ => null,
    };
}
