using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Parser;

namespace NinePSharp.Fog.Server.Tests;

internal sealed class ControlFixture : IDisposable
{
    private ushort tag = 1;

    private int effects;

    internal ControlFixture(FogNinePLimits? limits = null)
    {
        Policy = new FogNodePolicy(
            1,
            [
            new("worker", new string('1', 64), FogNodePolicy.SpkiPin(NodeCertificate), "worker.test"),
            new("other", new string('2', 64), FogNodePolicy.SpkiPin(OtherCertificate), "other.test"),
        ]);
        Store = new FogTransactionStore(new(4, 2, 4096, 4096, 32768, 3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)), Policy.IsCurrentOwner, Time);
        var service = new FogTransactionService(
            "fixture",
            Store,
            new HashSet<string> { "request", "payload" },
            new HashSet<string> { "reply", "extra" },
            (principal, id, cancellation) => Store.CommitAsync(
                principal.Owner,
                id,
                inputs => new FogCommitPlan(
                new Dictionary<string, byte[]> { ["reply"] = inputs["request"].ToArray(), ["extra"] = [9] }, async () =>
                {
                    Interlocked.Increment(ref effects);
                    if (Apply is not null)
                    {
                        await Apply();
                    }
                }),
                cancellation));
        Tree = new FogTransactionFileTree([service]);
        Limits = limits ?? new(4, 32, 8, 4096, 16384, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        Dispatcher = new(Tree, Policy, Limits, Time);
    }

    internal X509Certificate2 ServerCertificate { get; } = Certificate("control.test");

    internal X509Certificate2 NodeCertificate { get; } = Certificate("worker.test");

    internal X509Certificate2 OtherCertificate { get; } = Certificate("other.test");

    internal FogNodePolicy Policy { get; }

    internal FogTransactionStore Store { get; }

    internal FogTransactionFileTree Tree { get; }

    internal FogNinePDispatcher Dispatcher { get; }

    internal ManualTime Time { get; } = new();

    internal FogNinePLimits Limits { get; }

    internal int Effects => Volatile.Read(ref effects);

    internal Func<Task>? Apply { get; set; }

    internal string Owner => Policy.Attach("worker", NodeCertificate).Owner;

    public void Dispose()
    {
        Dispatcher.CloseSessionAsync("s").GetAwaiter().GetResult();
        ServerCertificate.Dispose();
        NodeCertificate.Dispose();
        OtherCertificate.Dispose();
    }

    internal static X509Certificate2 Certificate(string name, bool includeSan = true, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        if (includeSan)
        {
            request.CertificateExtensions.Add(san.Build());
        }

        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        return request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-1), notAfter ?? DateTimeOffset.UtcNow.AddHours(1));
    }

    internal Task<object> Send(NinePMessage message, string session = "s", X509Certificate2? certificate = null) =>
        Dispatcher.DispatchAsync(session, message, NinePDialect.NineP2000, certificate ?? NodeCertificate);

    internal async Task Initialize(string session = "s")
    {
        _ = await Send(NinePMessage.NewMsgTversion(new Tversion(65535, 4096, "9P2000")), session);
        _ = await Send(NinePMessage.NewMsgTattach(new Tattach(NextTag(), 1, NinePConstants.NoFid, "worker", "runtime")), session);
    }

    internal ushort NextTag() => tag++;

    internal Task<object> Walk(uint fid, uint next, params string[] path) => Send(NinePMessage.NewMsgTwalk(new Twalk(NextTag(), fid, next, path)));

    internal Task<object> Open(uint fid, byte mode) => Send(NinePMessage.NewMsgTopen(new Topen(NextTag(), fid, mode)));

    internal Task<object> Read(uint fid, ulong offset = 0, uint count = 4096) => Send(NinePMessage.NewMsgTread(new Tread(NextTag(), fid, offset, count)));

    internal Task<object> Write(uint fid, byte[] bytes, ulong offset = 0) => Send(NinePMessage.NewMsgTwrite(new Twrite(NextTag(), fid, offset, bytes)));

    internal Task<object> Clunk(uint fid) => Send(NinePMessage.NewMsgTclunk(new Tclunk(NextTag(), fid)));

    internal async Task<string> Clone()
    {
        _ = await Walk(1, 2, "control", "fixture", "clone");
        _ = await Open(2, NinePConstants.OREAD);
        var reply = (Rread)await Read(2);
        _ = await Clunk(2);
        return System.Text.Encoding.ASCII.GetString(reply.Data.Span).TrimEnd('\n');
    }

    internal async Task Upload(string id, byte[] bytes)
    {
        _ = await Walk(1, 3, "control", "fixture", id, "request");
        _ = await Open(3, NinePConstants.OWRITE);
        _ = await Write(3, bytes);
        _ = await Clunk(3);
        _ = await Walk(1, 4, "control", "fixture", id, "ctl");
        _ = await Open(4, NinePConstants.OWRITE);
    }

    internal FogNodeListener Listen()
    {
        var listener = new FogNodeListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate,
            Policy,
            Dispatcher,
            NullLogger.Instance,
            4,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(200));
        listener.Start();
        return listener;
    }

    internal sealed class ManualTime : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
