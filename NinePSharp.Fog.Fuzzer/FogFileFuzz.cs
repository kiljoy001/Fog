using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Parser;

namespace NinePSharp.Fuzzer;

/// <summary>Control-fid model with generated fragmentation, rejected offsets, abort and exactly-once effects.</summary>
public static class FogFileFuzz
{
    private static readonly X509Certificate2 Certificate = MakeCertificate();
    private static readonly FogNodePolicy Policy = new(1,
        [new("worker", new string('1', 64), FogNodePolicy.SpkiPin(Certificate), "worker.test")]);

    public static void Run(Stream input)
    {
        byte[] buffer = new byte[2048];
        int length = input.ReadAtLeast(buffer, buffer.Length, false);
        Run(buffer.AsSpan(0, length));
    }

    public static void Run(ReadOnlySpan<byte> source)
    {
        byte[] bytes = source[..Math.Min(source.Length, 2048)].ToArray();
        int stride = bytes.Length == 0 ? 1 : bytes[0] + 1;
        var store = new FogTransactionStore(new(2, 2, 4096, 4096, 16384, 2, TimeSpan.FromHours(1), TimeSpan.FromHours(1)), Policy.IsCurrentOwner);
        int effects = 0;
        var service = new FogTransactionService("fixture", store, new HashSet<string> { "request" }, new HashSet<string> { "reply" },
            (principal, id, cancellation) => store.CommitAtomicAsync(principal.Owner, id, files =>
                new(new Dictionary<string, byte[]> { ["reply"] = files["request"].ToArray() }, () => effects++), cancellation));
        var dispatcher = new FogNinePDispatcher(new FogTransactionFileTree([service]), Policy,
            new(1, 8, 2, 4096, 16384, TimeSpan.FromHours(1), TimeSpan.FromHours(1)));
        ushort tag = 0;
        object Send(NinePMessage message) => dispatcher.DispatchAsync("fuzz", message, NinePDialect.NineP2000, Certificate).GetAwaiter().GetResult();
        object Walk(uint fid, params string[] path) => Send(NinePMessage.NewMsgTwalk(new Twalk(tag++, 1, fid, path)));
        object Open(uint fid, byte mode) => Send(NinePMessage.NewMsgTopen(new Topen(tag++, fid, mode)));
        object Clunk(uint fid) => Send(NinePMessage.NewMsgTclunk(new Tclunk(tag++, fid)));
        object Read(uint fid, ulong offset, uint count) => Send(NinePMessage.NewMsgTread(new Tread(tag++, fid, offset, count)));
        object Write(uint fid, ulong offset, byte[] content) => Send(NinePMessage.NewMsgTwrite(new Twrite(tag++, fid, offset, content)));
        void Initialize()
        {
            Expect<Rversion>(Send(NinePMessage.NewMsgTversion(new Tversion(65535, 4096, "9P2000"))));
            Expect<Rattach>(Send(NinePMessage.NewMsgTattach(new Tattach(tag++, 1, NinePConstants.NoFid, "worker", "runtime"))));
        }

        try
        {
            Initialize();
            Reject("tx-expired", Walk(2, "missing"));
            if (store.LiveIds().Count != 0) throw new InvalidOperationException("walk allocated a transaction");
            Expect<Rwalk>(Walk(2, "control", "fixture", "clone"));
            Reject("denied", Open(2, NinePConstants.OWRITE));
            Expect<Ropen>(Open(2, NinePConstants.OREAD));
            string id = Encoding.ASCII.GetString(Expect<Rread>(Read(2, 0, 128)).Data.Span).TrimEnd('\n');
            Expect<Rclunk>(Clunk(2));
            Expect<Rwalk>(Walk(4, "control", "fixture", id, "ctl"));
            Expect<Ropen>(Open(4, NinePConstants.OWRITE));
            Reject("invalid-request", Write(4, 0, [0]));
            Reject("upload-open", Write(4, 0, "commit\n"u8.ToArray()));
            Expect<Rwalk>(Walk(3, "control", "fixture", id, "request"));
            Expect<Ropen>(Open(3, NinePConstants.OWRITE));
            Reject("invalid-request", Write(3, ulong.MaxValue, [1]));
            for (int offset = 0; offset < bytes.Length; offset += stride)
            {
                byte[] fragment = bytes.Skip(offset).Take(stride).ToArray();
                if (Expect<Rwrite>(Write(3, (ulong)offset, fragment)).Count != fragment.Length) throw new InvalidOperationException("partial upload");
            }
            Reject("upload-open", Write(4, 0, "commit\n"u8.ToArray()));
            if (effects != 0) throw new InvalidOperationException("effect before seal");
            Expect<Rclunk>(Clunk(3));
            if (bytes.Length != 0 && (bytes[0] & 1) != 0)
            {
                Expect<Rwalk>(Walk(3, "control", "fixture", id, "request"));
                Expect<Ropen>(Open(3, NinePConstants.OWRITE));
                Expect<Rwrite>(Write(3, 0, [99]));
                Initialize(); // Abort the replacement; retain the original sealed request.
                Expect<Rwalk>(Walk(4, "control", "fixture", id, "ctl"));
                Expect<Ropen>(Open(4, NinePConstants.OWRITE));
            }
            for (int retry = 0; retry < 2; retry++)
                if (Expect<Rwrite>(Write(4, 0, "commit\n"u8.ToArray())).Count != 7) throw new InvalidOperationException("commit count");
            if (effects != 1) throw new InvalidOperationException("repeated effect");
            Expect<Rwalk>(Walk(5, "control", "fixture", id, "reply"));
            Expect<Ropen>(Open(5, NinePConstants.OREAD));
            using var result = new MemoryStream();
            while (true)
            {
                var read = Expect<Rread>(Read(5, (ulong)result.Length, (uint)stride));
                if (read.Count == 0) break;
                if (result.Length + read.Count > bytes.Length) throw new InvalidOperationException("unbounded result");
                result.Write(read.Data.Span);
            }
            if (!bytes.SequenceEqual(result.ToArray())) throw new InvalidOperationException("snapshot differs from independent input model");
            if (Expect<Rread>(Read(5, ulong.MaxValue, uint.MaxValue)).Count != 0) throw new InvalidOperationException("invalid EOF");
            Expect<Rwrite>(Write(4, 0, "release\n"u8.ToArray()));
            Reject("tx-expired", Read(5, 0, 1));
            if (store.LiveIds().Count != 0) throw new InvalidOperationException("reservation leaked");
        }
        finally { dispatcher.CloseSessionAsync("fuzz").GetAwaiter().GetResult(); }
    }

    private static T Expect<T>(object value) where T : struct => value is T expected ? expected :
        throw new InvalidOperationException("unexpected response: " + value.GetType().Name + (value is Rerror error ? ":" + error.Ename : ""));

    private static void Reject(string code, object value)
    {
        if (Expect<Rerror>(value).Ename != code) throw new InvalidOperationException("unexpected rejection");
    }

    private static X509Certificate2 MakeCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=worker.test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("worker.test");
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
