using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dp9ik;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// The keyfs(4) tree over 9P2000, following 9front sys/src/cmd/auth/keyfs.c: a root whose
/// directories are users, each holding key, aeskey, pakhash, secret, log, status, expire and
/// warnings. Every change except log is saved before its request succeeds; a failed save leaves the
/// database as it was.
/// </summary>
internal sealed class KeyFsDispatcher : INinePFSDispatcher, INinePSessionLifecycle
{
    internal const string DatabaseWriteFailed = "keyfs: database write failed";
    private const uint DMDIR = 0x80000000;
    private const byte OWRITE = 1;
    private const byte OTRUNC = 0x10;
    private const int MaxWalkElements = 16;
    private const int MaxBadAttempts = 10;
    private const uint MessageSize = 8192 + 24;
    private const string Owner = "auth";
    private static readonly string[] FileNames = ["key", "aeskey", "pakhash", "secret", "log", "status", "expire", "warnings"];

    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<uint, Fid>> sessions = new(StringComparer.Ordinal);
    private readonly Action<KeyDatabase> save;
    private readonly TimeProvider time;
    private KeyDatabase database;

    internal KeyFsDispatcher(KeyDatabase database, Action<KeyDatabase> save, TimeProvider time)
    {
        this.database = database;
        this.save = save;
        this.time = time;
    }

    private enum Node
    {
        Root,
        User,
        Key,
        AesKey,
        PakHash,
        Secret,
        Log,
        Status,
        Expire,
        Warnings,
    }

    internal int SessionCount
    {
        get
        {
            lock (gate)
            {
                return sessions.Count;
            }
        }
    }

    public Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
    {
        lock (gate)
        {
            Dictionary<uint, Fid> fids = Fids(sessionId);
            object response = message switch
            {
                NinePMessage.MsgTversion version => Version(fids, version.Item),
                NinePMessage.MsgTauth auth => new Rerror(auth.Item.Tag, "keyfs: authentication not required"),
                NinePMessage.MsgTattach attach => Attach(fids, attach.Item),
                NinePMessage.MsgTflush flush => new Rflush(flush.Item.Tag),
                NinePMessage.MsgTwalk walk => Walk(fids, walk.Item),
                NinePMessage.MsgTopen open => Open(fids, open.Item),
                NinePMessage.MsgTcreate create => Create(fids, create.Item),
                NinePMessage.MsgTread read => Read(fids, read.Item),
                NinePMessage.MsgTwrite write => Write(fids, write.Item),
                NinePMessage.MsgTclunk clunk => Clunk(fids, clunk.Item),
                NinePMessage.MsgTremove remove => Remove(fids, remove.Item),
                NinePMessage.MsgTstat stat => Stat(fids, stat.Item),
                NinePMessage.MsgTwstat wstat => Wstat(fids, wstat.Item),

                // Thrown so the connection replies with the request's own tag.
                _ => throw new NotSupportedException("bad fcall type"),
            };
            return Task.FromResult(response);
        }
    }

    public Task CloseSessionAsync(string sessionId)
    {
        lock (gate)
        {
            sessions.Remove(sessionId);
        }

        return Task.CompletedTask;
    }

    internal AuthKey? FindKey(string name)
    {
        KeyUser? user;
        lock (gate)
        {
            user = database.Find(name);
            if (user is null || ReadRefusal(user) is not null)
            {
                return null;
            }

            user = user.Copy();
        }

        AuthKey key = AuthKey.FromKeys(user.DesKey, user.AesKey);
        key.ApplyAuthPakHash(user.Name);
        return key;
    }

    // setkey: the DES key, and the AES key unless it is zero, which recomputes the PAK hash.
    internal bool SetKey(string name, AuthKey key)
    {
        lock (gate)
        {
            return database.Find(name) is not null && Commit(next =>
            {
                KeyUser user = next.Find(name)!;
                user.DesKey = key.DesKey;
                if (key.AesKey.Any(value => value != 0))
                {
                    user.AesKey = key.AesKey;
                    user.Rehash();
                }
            }) is null;
        }
    }

    internal bool SetSecret(string name, string secret)
    {
        lock (gate)
        {
            return database.Find(name) is not null && Commit(next => next.Find(name)!.Secret = Encoding.UTF8.GetBytes(secret)) is null;
        }
    }

    // succeed: "good" to the user's log, which clears the bad-attempt count.
    internal void Succeed(string name)
    {
        lock (gate)
        {
            database.Find(name)?.Bad = 0;
        }
    }

    private static Rversion Version(Dictionary<uint, Fid> fids, Tversion request)
    {
        fids.Clear();
        string version = request.Version.StartsWith("9P", StringComparison.Ordinal) ? "9P2000" : "unknown";
        return new Rversion(request.Tag, Math.Min(request.MSize, MessageSize), version);
    }

    private static Rattach Attach(Dictionary<uint, Fid> fids, Tattach request)
    {
        fids[request.Fid] = new Fid(Node.Root, 0);
        return new Rattach(request.Tag, QidOf(Node.Root, 0));
    }

    private static object Clunk(Dictionary<uint, Fid> fids, Tclunk request)
        => fids.Remove(request.Fid) ? new Rclunk(request.Tag) : new Rerror(request.Tag, "clunk of unused fid");

    private static Rread DirectoryRead(Tread request, IEnumerable<Stat> entries)
    {
        // Directory reads return whole entries from a stat boundary, as dirread(2) expects.
        var data = new List<byte>();
        ulong position = 0;
        foreach (Stat entry in entries)
        {
            var bytes = new byte[entry.Size];
            int offset = 0;
            entry.WriteTo(bytes, ref offset);
            if (position >= request.Offset)
            {
                if ((ulong)(data.Count + bytes.Length) > request.Count)
                {
                    break;
                }

                data.AddRange(bytes);
            }

            position += (ulong)bytes.Length;
        }

        return new Rread(request.Tag, data.ToArray());
    }

    private static Qid QidOf(Node node, ulong uniq)
        => new(node is Node.Root or Node.User ? QidType.QTDIR : QidType.QTFILE, 0, (ulong)node | (uniq * 0x100));

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value + "\n");

    private static string FirstLine(byte[] data) => Encoding.UTF8.GetString(data).Split('\n')[0];

    private static byte LeadingNumber(string text)
    {
        ulong value = 0;
        foreach (char digit in text.TrimStart().TakeWhile(char.IsAsciiDigit))
        {
            value = unchecked((value * 10) + (ulong)(digit - '0'));
        }

        return unchecked((byte)value);
    }

    private Dictionary<uint, Fid> Fids(string sessionId)
    {
        if (!sessions.TryGetValue(sessionId, out Dictionary<uint, Fid>? fids))
        {
            fids = new Dictionary<uint, Fid>();
            sessions.Add(sessionId, fids);
        }

        return fids;
    }

    private object Walk(Dictionary<uint, Fid> fids, Twalk request)
    {
        if (!fids.TryGetValue(request.Fid, out Fid? fid))
        {
            return new Rerror(request.Tag, "walk of unused fid");
        }

        if (request.NewFid != request.Fid && fids.ContainsKey(request.NewFid))
        {
            return new Rerror(request.Tag, "fid in use");
        }

        if (request.Wname.Length > MaxWalkElements)
        {
            return new Rerror(request.Tag, "too many path name elements");
        }

        Node node = fid.Node;
        ulong uniq = fid.Uniq;
        var qids = new List<Qid>();
        foreach (string name in request.Wname)
        {
            string? error = Step(ref node, ref uniq, name);
            if (error is not null)
            {
                return qids.Count == 0 ? new Rerror(request.Tag, error) : new Rwalk(request.Tag, qids.ToArray());
            }

            qids.Add(QidOf(node, uniq));
        }

        fids[request.NewFid] = new Fid(node, uniq);
        return new Rwalk(request.Tag, qids.ToArray());
    }

    private string? Step(ref Node node, ref ulong uniq, string name)
    {
        switch (node)
        {
            case Node.Root:
                if (name == "..")
                {
                    return null;
                }

                KeyUser? user = database.Find(name);
                if (user is null)
                {
                    return "file not found";
                }

                (node, uniq) = (Node.User, user.Uniq);
                return null;
            case Node.User:
                if (name == "..")
                {
                    (node, uniq) = (Node.Root, 0);
                    return null;
                }

                int index = Array.IndexOf(FileNames, name);
                if (index < 0)
                {
                    return "file not found";
                }

                node = Node.Key + index;
                return null;
            default:
                return "file is not a directory";
        }
    }

    private object Open(Dictionary<uint, Fid> fids, Topen request)
    {
        if (!fids.TryGetValue(request.Fid, out Fid? fid))
        {
            return new Rerror(request.Tag, "open of unused fid");
        }

        if (fid.Node != Node.Root && database.FindByUniq(fid.Uniq) is null)
        {
            return new Rerror(request.Tag, "user removed");
        }

        if (fid.Node == Node.User && (request.Mode & (OWRITE | OTRUNC)) != 0)
        {
            return new Rerror(request.Tag, "user already exists");
        }

        return new Ropen(request.Tag, QidOf(fid.Node, fid.Uniq), MessageSize - 24);
    }

    private object Create(Dictionary<uint, Fid> fids, Tcreate request)
    {
        if (!fids.TryGetValue(request.Fid, out Fid? fid))
        {
            return new Rerror(request.Tag, "create of unused fid");
        }

        if (fid.Node != Node.Root || (request.Perm & DMDIR) == 0)
        {
            return new Rerror(request.Tag, "permission denied");
        }

        string name = request.Name;
        if (name.Length == 0)
        {
            return new Rerror(request.Tag, "empty file name");
        }

        if (Encoding.UTF8.GetByteCount(name) >= KeyDatabase.NameLength)
        {
            return new Rerror(request.Tag, "file name too long");
        }

        if (database.Find(name) is not null)
        {
            return new Rerror(request.Tag, "user already exists");
        }

        if (!KeyDatabase.IsValidName(name))
        {
            return new Rerror(request.Tag, "bad user name");
        }

        ulong uniq = 0;
        string? error = Commit(next => uniq = next.Add(name).Uniq);
        if (error is not null)
        {
            return new Rerror(request.Tag, error);
        }

        fids[request.Fid] = new Fid(Node.User, uniq);
        return new Rcreate(request.Tag, QidOf(Node.User, uniq), MessageSize - 24);
    }

    private object Read(Dictionary<uint, Fid> fids, Tread request)
    {
        if (!fids.TryGetValue(request.Fid, out Fid? fid))
        {
            return new Rerror(request.Tag, "read of unused fid");
        }

        if (fid.Node == Node.Root)
        {
            return DirectoryRead(request, database.Users.Select(user => StatOf(Node.User, user)));
        }

        KeyUser? user = database.FindByUniq(fid.Uniq);
        if (user is null)
        {
            return new Rerror(request.Tag, "user removed");
        }

        if (fid.Node == Node.User)
        {
            return DirectoryRead(request, FileNames.Select((_, index) => StatOf(Node.Key + index, user)));
        }

        long now = Now();
        byte[] data;
        switch (fid.Node)
        {
            case Node.Key or Node.AesKey or Node.PakHash or Node.Secret:
                if (ReadRefusal(user) is string refusal)
                {
                    return new Rerror(request.Tag, refusal);
                }

                data = fid.Node switch
                {
                    Node.Key => user.DesKey,
                    Node.AesKey => user.AesKey,
                    Node.PakHash => user.PakHash,
                    _ => user.Secret,
                };
                break;
            case Node.Status:
                data = Text(!user.Disabled && user.Expire != 0 && user.Expire < now ? "expired" : user.Disabled ? "disabled" : "ok");
                break;
            case Node.Expire:
                data = Text(user.Expire == 0 ? "never" : user.Expire.ToString(CultureInfo.InvariantCulture));
                break;
            case Node.Log:
                data = Text(user.Bad.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                data = Text(user.Warnings.ToString(CultureInfo.InvariantCulture));
                break;
        }

        int offset = (int)Math.Min(request.Offset, (ulong)data.Length);
        int count = (int)Math.Min(request.Count, (uint)(data.Length - offset));
        return new Rread(request.Tag, data.AsMemory(offset, count));
    }

    private object Write(Dictionary<uint, Fid> fids, Twrite request)
    {
        if (!fids.TryGetValue(request.Fid, out Fid? fid))
        {
            return new Rerror(request.Tag, "permission denied");
        }

        if (fid.Node is Node.Root or Node.User)
        {
            return new Rerror(request.Tag, "permission denied");
        }

        KeyUser? current = database.FindByUniq(fid.Uniq);
        if (current is null)
        {
            return new Rerror(request.Tag, "user removed");
        }

        byte[] data = request.Data.ToArray();
        if (fid.Node == Node.Log)
        {
            // The bad-attempt count is kept in memory only, as in keyfs.
            current.Bad = Encoding.UTF8.GetString(data) == "good" ? 0 : current.Bad + 1;
            if (current.Bad != 0 && current.Bad % MaxBadAttempts == 0)
            {
                current.PurgatoryEnds = Now() + (long)current.Bad;
            }

            return new Rwrite(request.Tag, (uint)data.Length);
        }

        string line = FirstLine(data);
        string? error = fid.Node switch
        {
            Node.Key when data.Length != Dp9ikConstants.DesKeyLength => "garbled write data",
            Node.AesKey when data.Length != Dp9ikConstants.AesKeyLength => "garbled write data",
            Node.Secret when data.Length >= KeyDatabase.SecretLength => "garbled write data",
            Node.Status when line is not ("ok" or "disabled") => "unknown status",
            Node.Expire when line != "never" && !uint.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out _) => "bad expiration date",
            Node.PakHash => "permission denied",
            _ => null,
        };
        if (error is not null)
        {
            return new Rerror(request.Tag, error);
        }

        error = Commit(next =>
        {
            KeyUser user = next.FindByUniq(fid.Uniq)!;
            switch (fid.Node)
            {
                case Node.Key:
                    user.DesKey = data;
                    break;
                case Node.AesKey:
                    user.AesKey = data;
                    user.Rehash();
                    break;
                case Node.Secret:
                    user.Secret = data;
                    break;
                case Node.Status:
                    user.Disabled = line == "disabled";
                    user.Bad = 0;
                    break;
                case Node.Expire:
                    user.Expire = line == "never" ? 0 : uint.Parse(line, NumberStyles.None, CultureInfo.InvariantCulture);
                    user.Warnings = 0;
                    break;
                default:
                    user.Warnings = LeadingNumber(line);
                    break;
            }
        });
        return error is null ? new Rwrite(request.Tag, (uint)data.Length) : new Rerror(request.Tag, error);
    }

    private object Remove(Dictionary<uint, Fid> fids, Tremove request)
    {
        // remove(5): the fid is clunked whether or not the remove succeeds.
        if (!fids.Remove(request.Fid, out Fid? fid))
        {
            return new Rerror(request.Tag, "permission denied");
        }

        if (database.FindByUniq(fid.Uniq) is null && fid.Node != Node.Root)
        {
            return new Rerror(request.Tag, "user removed");
        }

        string? error = fid.Node switch
        {
            Node.User => Commit(next => next.Remove(next.FindByUniq(fid.Uniq)!)),
            Node.Warnings => Commit(next => next.FindByUniq(fid.Uniq)!.Warnings = 0),
            _ => "permission denied",
        };
        return error is null ? new Rremove(request.Tag) : new Rerror(request.Tag, error);
    }

    private object Stat(Dictionary<uint, Fid> fids, Tstat request)
    {
        if (!fids.TryGetValue(request.Fid, out Fid? fid))
        {
            return new Rerror(request.Tag, "stat on unattached fid");
        }

        if (fid.Node == Node.Root)
        {
            return new Rstat(request.Tag, StatOf(Node.Root, null));
        }

        KeyUser? user = database.FindByUniq(fid.Uniq);
        return user is null ? new Rerror(request.Tag, "user removed") : new Rstat(request.Tag, StatOf(fid.Node, user));
    }

    private object Wstat(Dictionary<uint, Fid> fids, Twstat request)
    {
        if (!fids.TryGetValue(request.Fid, out Fid? fid) || fid.Node != Node.User)
        {
            return new Rerror(request.Tag, "permission denied");
        }

        if (database.FindByUniq(fid.Uniq) is null)
        {
            return new Rerror(request.Tag, "user previously removed");
        }

        string name = request.Stat.Name;
        if (!KeyDatabase.IsValidName(name))
        {
            return new Rerror(request.Tag, "bad user name");
        }

        if (database.Find(name) is not null)
        {
            return new Rerror(request.Tag, "user already exists");
        }

        string? error = Commit(next => next.Rename(next.FindByUniq(fid.Uniq)!, name));
        return error is null ? new Rwstat(request.Tag) : new Rerror(request.Tag, error);
    }

    private string? Commit(Action<KeyDatabase> change)
    {
        KeyDatabase next = database.Copy();
        change(next);
        try
        {
            save(next);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DatabaseWriteFailed;
        }

        database = next;
        return null;
    }

    private Stat StatOf(Node node, KeyUser? user)
    {
        uint now = (uint)Now();
        bool directory = node is Node.Root or Node.User;
        string name = node switch
        {
            Node.Root => "keys",
            Node.User => user!.Name,
            _ => FileNames[node - Node.Key],
        };
        return new Stat(0, 0, 0, QidOf(node, user?.Uniq ?? 0), directory ? DMDIR | 0x1FF : 0x1B6, now, now, 0, name, Owner, Owner, Owner);
    }

    private long Now() => time.GetUtcNow().ToUnixTimeSeconds();

    private string? ReadRefusal(KeyUser user)
    {
        long now = Now();
        if (user.Disabled)
        {
            return "user disabled";
        }

        if (user.PurgatoryEnds > now)
        {
            return "user in purgatory";
        }

        return user.Expire != 0 && user.Expire < now ? "user expired" : null;
    }

    private sealed record Fid(Node Node, ulong Uniq);
}
