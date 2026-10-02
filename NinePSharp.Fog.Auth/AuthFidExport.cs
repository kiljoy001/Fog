using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// Authenticates attaches on another 9P export as 9front's lib9p does with factotum: Tauth opens
/// an afid speaking p9any with dp9ik under the keyfs key of the auth id, and Tattach with that afid
/// follows authattach. The export underneath asks <see cref="AuthenticatedUser"/> who an afid
/// authenticated. An attach without an afid passes through.
/// </summary>
public sealed class AuthFidExport : INinePFSDispatcher, INinePSessionLifecycle
{
    private static long authPath = long.MinValue;
    private readonly object gate = new();
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly INinePFSDispatcher inner;
    private readonly IAuthDatabase keys;
    private readonly AuthFidOptions options;

    public AuthFidExport(INinePFSDispatcher inner, KeyFsHost keys, AuthFidOptions options)
        : this(inner, (IAuthDatabase)keys, options)
    {
    }

    internal AuthFidExport(INinePFSDispatcher inner, IAuthDatabase keys, AuthFidOptions options)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    // The client user an afid's ticket named, once the afid is authenticated.
    public string? AuthenticatedUser(string sessionId, uint afid)
    {
        lock (gate)
        {
            return connections.TryGetValue(sessionId, out Connection? connection) &&
                connection.Afids.TryGetValue(afid, out Afid? auth) && auth.Authenticated
                ? auth.Exchange.ClientUser
                : null;
        }
    }

    public async Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
    {
        if (message is NinePMessage.MsgTversion)
        {
            lock (gate)
            {
                connections.Remove(sessionId);
            }

            return await inner.DispatchAsync(sessionId, message, dialect, certificate);
        }

        string? refusal;
        object? reply;
        lock (gate)
        {
            if (!connections.TryGetValue(sessionId, out Connection? connection))
            {
                connection = new Connection();
                connections.Add(sessionId, connection);
            }

            (refusal, reply) = Serve(connection, message);
        }

        if (refusal is not null)
        {
            return Error(Tag(message), dialect, refusal);
        }

        if (reply is not null)
        {
            return reply;
        }

        object response = await inner.DispatchAsync(sessionId, message, dialect, certificate);
        lock (gate)
        {
            if (connections.TryGetValue(sessionId, out Connection? connection))
            {
                Account(connection, message, response);
            }
        }

        return response;
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        lock (gate)
        {
            connections.Remove(sessionId);
        }

        if (inner is INinePSessionLifecycle lifecycle)
        {
            await lifecycle.CloseSessionAsync(sessionId);
        }
    }

    private static object Error(ushort tag, NinePDialect dialect, string message) => dialect switch
    {
        NinePDialect.NineP2000L => new Rlerror(tag, (uint)LinuxErrorCode.EACCESS),
        NinePDialect.NineP2000U => new Rerror(tag, message, (uint)LinuxErrorCode.EACCESS),
        _ => new Rerror(tag, message),
    };

    private static ushort Tag(NinePMessage message) => message switch
    {
        NinePMessage.MsgTauth m => m.Item.Tag,
        NinePMessage.MsgTattach m => m.Item.Tag,
        NinePMessage.MsgTwalk m => m.Item.Tag,
        NinePMessage.MsgTopen m => m.Item.Tag,
        NinePMessage.MsgTcreate m => m.Item.Tag,
        NinePMessage.MsgTread m => m.Item.Tag,
        NinePMessage.MsgTwrite m => m.Item.Tag,
        NinePMessage.MsgTclunk m => m.Item.Tag,
        NinePMessage.MsgTremove m => m.Item.Tag,
        NinePMessage.MsgTstat m => m.Item.Tag,
        NinePMessage.MsgTwstat m => m.Item.Tag,
        _ => NinePConstants.NoTag,
    };

    private static void Account(Connection connection, NinePMessage message, object response)
    {
        switch (message)
        {
            case NinePMessage.MsgTattach attach when response is Rattach:
                connection.Fids.Add(attach.Item.Fid);
                break;
            case NinePMessage.MsgTwalk walk when response is Rwalk walked && walked.Wqid.Length == walk.Item.Wname.Length:
                connection.Fids.Add(walk.Item.NewFid);
                break;
            case NinePMessage.MsgTclunk clunk when response is Rclunk:
                connection.Fids.Remove(clunk.Item.Fid);
                break;
            case NinePMessage.MsgTremove remove:
                connection.Fids.Remove(remove.Item.Fid);
                break;
        }
    }

    // authread: a message the count cannot hold is still consumed, and any failure is a botch.
    private static (string? Refusal, object? Reply) Read(Afid afid, Tread request)
    {
        string? refusal = AuthRead(afid, (int)request.Count, out byte[] data);
        return (refusal, new Rread(request.Tag, data));
    }

    private static string? AuthRead(Afid afid, int count, out byte[] data)
    {
        data = [];
        byte[]? next;
        try
        {
            next = afid.Exchange.Read();
        }
        catch (P9anyException)
        {
            return "authrpc botch";
        }

        if (next is not null)
        {
            data = next;
            return count < next.Length ? "authread count too small" : null;
        }

        if (afid.Exchange.ClientUser != afid.Uname)
        {
            return "auth uname mismatch";
        }

        afid.Authenticated = true;
        return null;
    }

    private static (string? Refusal, object? Reply) Write(Afid afid, Twrite request)
    {
        try
        {
            afid.Exchange.Write(request.Data.Span);
        }
        catch (P9anyException exception)
        {
            return (exception.IsPhaseError ? $"phase error {exception.Message}" : exception.Message, null);
        }

        return (null, new Rwrite(request.Tag, request.Count));
    }

    private (string? Refusal, object? Reply) Serve(Connection connection, NinePMessage message)
    {
        switch (message)
        {
            case NinePMessage.MsgTauth auth:
                return Auth(connection, auth.Item);
            case NinePMessage.MsgTattach attach:
                return (Attach(connection, attach.Item), null);
            case NinePMessage.MsgTwalk walk:
                return (Walk(connection, walk.Item), null);
        }

        uint? fid = message switch
        {
            NinePMessage.MsgTopen m => m.Item.Fid,
            NinePMessage.MsgTcreate m => m.Item.Fid,
            NinePMessage.MsgTread m => m.Item.Fid,
            NinePMessage.MsgTwrite m => m.Item.Fid,
            NinePMessage.MsgTclunk m => m.Item.Fid,
            NinePMessage.MsgTremove m => m.Item.Fid,
            NinePMessage.MsgTstat m => m.Item.Fid,
            NinePMessage.MsgTwstat m => m.Item.Fid,
            _ => null,
        };
        if (fid is not uint number || !connection.Afids.TryGetValue(number, out Afid? afid))
        {
            return (null, null);
        }

        switch (message)
        {
            case NinePMessage.MsgTread read:
                return Read(afid, read.Item);
            case NinePMessage.MsgTwrite write:
                return Write(afid, write.Item);
            case NinePMessage.MsgTclunk clunk:
                connection.Afids.Remove(number);
                return (null, new Rclunk(clunk.Item.Tag));
            case NinePMessage.MsgTremove:
                connection.Afids.Remove(number);
                return ("remove prohibited", null);
            case NinePMessage.MsgTstat:
                return ("stat prohibited", null);
            case NinePMessage.MsgTwstat:
                return ("wstat prohibited", null);
            default:
                return ("9P protocol botch", null);
        }
    }

    private (string? Refusal, object? Reply) Auth(Connection connection, Tauth request)
    {
        if (connection.InUse(request.Afid))
        {
            return ("duplicate fid", null);
        }

        if (string.IsNullOrEmpty(request.Uname))
        {
            return ("no uname", null);
        }

        var exchange = new P9anyServer(keys.FindKey(options.AuthId), options.AuthId, options.AuthDomain);
        connection.Afids.Add(request.Afid, new Afid(request.Uname, request.Aname, exchange));
        var qid = new Qid(QidType.QTAUTH, 0, unchecked((ulong)Interlocked.Increment(ref authPath)));
        return (null, new Rauth(request.Tag, qid));
    }

    // authattach, after lib9p's sattach has allocated the fid and found the afid.
    private string? Attach(Connection connection, Tattach request)
    {
        if (connection.InUse(request.Fid))
        {
            return "duplicate fid";
        }

        if (request.Afid == NinePConstants.NoFid)
        {
            return null;
        }

        if (!connection.Afids.TryGetValue(request.Afid, out Afid? afid))
        {
            return connection.Fids.Contains(request.Afid) ? "not an auth fid" : "unknown fid";
        }

        if (afid.Uname != request.Uname)
        {
            return $"auth uname mismatch: {afid.Uname} vs {request.Uname}";
        }

        if (afid.Aname != request.Aname)
        {
            return $"auth aname mismatch: {afid.Aname} vs {request.Aname}";
        }

        return AuthRead(afid, 0, out _);
    }

    // swalk: an afid is open, and its number is taken.
    private string? Walk(Connection connection, Twalk request)
    {
        if (connection.Afids.ContainsKey(request.Fid))
        {
            return "cannot clone open fid";
        }

        return connection.Afids.ContainsKey(request.NewFid) ? "duplicate fid" : null;
    }

    private sealed class Connection
    {
        internal Dictionary<uint, Afid> Afids { get; } = new();

        internal HashSet<uint> Fids { get; } = new();

        internal bool InUse(uint fid) => Afids.ContainsKey(fid) || Fids.Contains(fid);
    }

    private sealed class Afid(string uname, string aname, P9anyServer exchange)
    {
        internal string Uname { get; } = uname;

        internal string Aname { get; } = aname;

        internal P9anyServer Exchange { get; } = exchange;

        internal bool Authenticated { get; set; }
    }
}
