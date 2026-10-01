using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Fog;
using NinePSharp.Fog.Server;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;

namespace NinePSharp.Fuzzer;

/// <summary>Arbitrary 9P request sequences against the Fog dispatcher, checked by an independent session model.</summary>
public static class FogDispatcherFuzz
{
    private const string Session = "fuzz";
    private static readonly X509Certificate2 Worker = MakeCertificate("worker.test");
    private static readonly X509Certificate2 Other = MakeCertificate("other.test");
    private static readonly X509Certificate2 Stranger = MakeCertificate("stranger.test");
    private static readonly X509Certificate2[] Certificates = [Worker, Other, Stranger];
    private static readonly string[] Names =
        [".", "..", "control", "fixture", "clone", "request", "payload", "ctl", "status", "reply", "extra", "missing"];
    private static readonly string[] Unames = ["worker", "other", "nobody"];
    private static readonly string[] Versions = ["9P2000", "9P2000.L", "bad"];
    private static readonly uint[] MessageSizes = [255, 256, 4096, 65535];
    private static readonly HashSet<string> Codes =
        ["busy", "denied", "invalid-request", "limit", "not-ready", "snapshot-limit", "tx-expired", "upload-open"];

    public static void Run(Stream input)
    {
        byte[] buffer = new byte[4096];
        int length = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        Run(buffer.AsSpan(0, length));
    }

    public static void Run(ReadOnlySpan<byte> source) => _ = Execute(source);

    /// <summary>Returns acknowledged commit writes and applied effects, so seeds can prove which states they reach.</summary>
    public static (int Commits, int Effects) Execute(ReadOnlySpan<byte> source)
    {
        var bytes = new Cursor(source[..Math.Min(source.Length, 4096)].ToArray());
        var time = new ManualTime();
        var policy = new FogNodePolicy(1,
        [
            new("worker", new string('1', 64), FogNodePolicy.SpkiPin(Worker), "worker.test"),
            new("other", new string('2', 64), FogNodePolicy.SpkiPin(Other), "other.test"),
        ]);
        var store = new FogTransactionStore(new(4, 4, 256, 256, 4096, 2, TimeSpan.FromHours(1), TimeSpan.FromHours(1)), policy.IsCurrentOwner, time);
        var effects = new Dictionary<string, int>(StringComparer.Ordinal);
        var service = new FogTransactionService("fixture", store, new HashSet<string> { "request", "payload" }, new HashSet<string> { "reply", "extra" },
            (principal, id, cancellation) => store.CommitAsync(principal.Owner, id, inputs => new FogCommitPlan(
                new Dictionary<string, byte[]> { ["reply"] = inputs["request"].ToArray(), ["extra"] = [1] },
                () =>
                {
                    lock (effects) effects[id] = effects.GetValueOrDefault(id) + 1;
                    return Task.CompletedTask;
                }), cancellation));
        var limits = new FogNinePLimits(1, 6, 4, 4096, 2048, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        var dispatcher = new FogNinePDispatcher(new FogTransactionFileTree([service]), policy, limits, time);

        bool exists = false;
        bool ready = false;
        long created = 0;
        uint messageSize = 0;
        ushort nextTag = 0;
        int commits = 0;
        var owners = new Dictionary<uint, X509Certificate2>();

        bool Expired() => exists && time.GetElapsedTime(created) >= limits.SessionLifetime;

        try
        {
            for (int step = 0; step < 128 && !bytes.End; step++)
            {
                byte operation = bytes.Next();
                int kind = (operation & 0x0F) % 12;
                X509Certificate2 certificate = Certificates[((operation >> 4) & 3) % 3];
                bool trusted = ReferenceEquals(certificate, Worker);
                bool other = ReferenceEquals(certificate, Other);
                ushort tag = (operation & 0x80) != 0 && bytes.Next() == 0 ? NinePConstants.NoTag : nextTag++;
                if (nextTag == NinePConstants.NoTag) nextTag = 0;
                uint fid = Fid(bytes.Next());

                switch (kind)
                {
                    case 0:
                    {
                        uint size = MessageSizes[bytes.Next() % MessageSizes.Length];
                        string version = Versions[bytes.Next() % Versions.Length];
                        if (!exists) created = time.GetTimestamp();
                        exists = true;
                        object reply = Send(dispatcher, NinePMessage.NewMsgTversion(new Tversion(tag, size, version)), certificate);
                        ready = false;
                        owners.Clear();
                        if (size < 256) Rejected(reply, tag, "invalid-request");
                        else if (Expired()) Rejected(reply, tag, "denied");
                        else
                        {
                            var accepted = Expect<Rversion>(reply, tag);
                            messageSize = Math.Min(size, limits.MessageSize);
                            Check(accepted.MSize == messageSize, "negotiated msize differs from model");
                            Check(accepted.Version == (version.StartsWith("9P2000", StringComparison.Ordinal) ? "9P2000" : "unknown"), "version");
                            ready = accepted.Version == "9P2000";
                        }

                        continue;
                    }
                    case 1:
                        dispatcher.CloseSessionAsync(Session).GetAwaiter().GetResult();
                        exists = ready = false;
                        owners.Clear();
                        continue;
                    case 2:
                        time.Advance((bytes.Next() & 1) == 0 ? limits.SnapshotLifetime : limits.SessionLifetime);
                        continue;
                }

                NinePMessage message = kind switch
                {
                    3 => NinePMessage.NewMsgTattach(new Tattach(tag, fid, (bytes.Next() & 1) == 0 ? NinePConstants.NoFid : 0,
                        Unames[bytes.Next() % Unames.Length], (bytes.Next() & 1) == 0 ? "runtime" : "")),
                    4 => NinePMessage.NewMsgTwalk(new Twalk(tag, fid, Fid(bytes.Next()), Path(bytes, store))),
                    5 => NinePMessage.NewMsgTopen(new Topen(tag, fid, (byte)(bytes.Next() % 4))),
                    6 => NinePMessage.NewMsgTread(new Tread(tag, fid, bytes.Next(), (uint)bytes.Next() * 32)),
                    7 => NinePMessage.NewMsgTwrite(new Twrite(tag, fid, (ulong)(bytes.Next() % 8), Payload(bytes))),
                    8 => NinePMessage.NewMsgTclunk(new Tclunk(tag, fid)),
                    9 => NinePMessage.NewMsgTstat(new Tstat(tag, fid)),
                    10 => NinePMessage.NewMsgTflush(new Tflush(tag, (bytes.Next() & 1) == 0 ? tag : (ushort)(tag + 1))),
                    _ => Unsupported(bytes.Next(), tag, fid),
                };

                int before = TotalEffects(effects);
                object response = Send(dispatcher, message, certificate);
                bool unsupported = kind == 11;
                bool flush = kind == 10;

                if (!exists || !ready)
                {
                    Rejected(response, tag, "not-ready");
                    continue;
                }

                if (Expired())
                {
                    Rejected(response, tag, "denied");
                    continue;
                }

                if (tag == NinePConstants.NoTag)
                {
                    Rejected(response, tag, "invalid-request");
                    continue;
                }

                if (unsupported)
                {
                    Rejected(response, tag, "denied");
                    continue;
                }

                if (flush)
                {
                    var request = ((NinePMessage.MsgTflush)message).Item;
                    if (request.OldTag == request.Tag) Rejected(response, tag, "invalid-request");
                    else Expect<Rflush>(response, tag);
                    continue;
                }

                Check(((ISerializable)response).Size <= messageSize, "reply exceeds negotiated msize");
                if (response is Rerror error)
                {
                    Check(error.Tag == tag, "error tag differs from request");
                    Check(Codes.Contains(error.Ename), "undeclared or private error: " + error.Ename);
                }
                else
                {
                    Check(((ISerializable)response).Tag == tag, "reply tag differs from request");
                    Check(response is Rattach || (owners.TryGetValue(fid, out var owner) && ReferenceEquals(owner, certificate)),
                        "a fid was used without its attaching certificate");
                }

                // Clunk releases the fid even when the policy check fails (clunk(5): the fid is always gone).
                if (message is NinePMessage.MsgTclunk && !(response is Rerror { Ename: "invalid-request" })) owners.Remove(fid);

                switch (response)
                {
                    case Rattach:
                        Check(!owners.ContainsKey(fid), "attach replaced a live fid");
                        owners[fid] = certificate;
                        Check(message is NinePMessage.MsgTattach attach && attach.Item.Aname == "runtime" &&
                            attach.Item.Afid == NinePConstants.NoFid && (trusted ? attach.Item.Uname == "worker" : other && attach.Item.Uname == "other"),
                            "attach accepted an unenrolled identity");
                        break;
                    case Rwalk walk:
                        var names = ((NinePMessage.MsgTwalk)message).Item.Wname;
                        Check(walk.Wqid.Length <= names.Length && (names.Length == 0 || walk.Wqid.Length != 0), "walk qid count");
                        uint newFid = ((NinePMessage.MsgTwalk)message).Item.NewFid;
                        if (walk.Wqid.Length == names.Length) owners[newFid] = certificate;
                        break;
                    case Rread read:
                        Check(read.Count <= ((NinePMessage.MsgTread)message).Item.Count && read.Count == read.Data.Length, "read exceeds request");
                        break;
                    case Rwrite written:
                        Check(written.Count <= ((NinePMessage.MsgTwrite)message).Item.Data.Length, "write count exceeds data");
                        if (written.Count == 7 && ((NinePMessage.MsgTwrite)message).Item.Data.Span.SequenceEqual("commit\n"u8)) commits++;
                        break;
                }

                lock (effects)
                {
                    Check(effects.Values.All(count => count == 1), "transaction effect applied more than once");
                    Check(TotalEffects(effects) <= commits, "effect without an acknowledged commit");
                    Check(TotalEffects(effects) == before || response is Rwrite, "an effect without a ctl write");
                }
            }
        }
        finally
        {
            dispatcher.CloseSessionAsync(Session).GetAwaiter().GetResult();
        }

        return (commits, TotalEffects(effects));
    }

    private static object Send(FogNinePDispatcher dispatcher, NinePMessage message, X509Certificate2 certificate)
    {
        object response = dispatcher.DispatchAsync(Session, message, NinePDialect.NineP2000, certificate).GetAwaiter().GetResult();
        Check(response is ISerializable, "dispatcher returned a non-message");
        return response;
    }

    private static NinePMessage Unsupported(byte kind, ushort tag, uint fid) => (kind % 4) switch
    {
        0 => NinePMessage.NewMsgTauth(new Tauth(tag, fid, "worker", "runtime")),
        1 => NinePMessage.NewMsgTcreate(new Tcreate(tag, fid, "created", 0x1A4, NinePConstants.OWRITE)),
        2 => NinePMessage.NewMsgTremove(new Tremove(tag, fid)),
        _ => NinePMessage.NewMsgTwstat(new Twstat(tag, fid,
            new Stat(0, 0, 0, new Qid(QidType.QTFILE, 0, 0), 0, 0, 0, 0, "renamed", "fog", "fog", "fog"))),
    };

    private static uint Fid(byte value) => value == 0xFF ? NinePConstants.NoFid : (uint)(value % 8);

    private static string[] Path(Cursor bytes, FogTransactionStore store)
    {
        int count = bytes.Next() % 6;
        var live = store.LiveIds().Order(StringComparer.Ordinal).ToArray();
        var path = new string[count];
        for (int index = 0; index < count; index++)
        {
            byte choice = bytes.Next();
            path[index] = choice >= 0xF0 && live.Length != 0 ? live[choice % live.Length] : Names[choice % Names.Length];
        }

        return path;
    }

    private static byte[] Payload(Cursor bytes) => (bytes.Next() % 4) switch
    {
        0 => "commit\n"u8.ToArray(),
        1 => "release\n"u8.ToArray(),
        2 => [],
        _ => bytes.Take(bytes.Next() % 17),
    };

    private static int TotalEffects(Dictionary<string, int> effects)
    {
        lock (effects) return effects.Values.Sum();
    }

    private static T Expect<T>(object value, ushort tag) where T : ISerializable
    {
        Check(value is T, "unexpected response: " + value.GetType().Name + (value is Rerror error ? ":" + error.Ename : ""));
        Check(((T)value).Tag == tag, "reply tag differs from request");
        return (T)value;
    }

    private static void Rejected(object value, ushort tag, string code)
        => Check(Expect<Rerror>(value, tag).Ename == code, "expected " + code + " but received " + ((Rerror)value).Ename);

    private static void Check(bool condition, string invariant)
    {
        if (!condition) throw new InvalidOperationException(invariant);
    }

    private static X509Certificate2 MakeCertificate(string name)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class Cursor(byte[] bytes)
    {
        private int position;

        internal bool End => position >= bytes.Length;

        internal byte Next() => position < bytes.Length ? bytes[position++] : (byte)0;

        internal byte[] Take(int count)
        {
            byte[] result = bytes.Skip(position).Take(count).ToArray();
            position += result.Length;
            return result;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
