using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NinePSharp.Fog.Server;

/// <summary>Immutable policy replacement fences old roots; credentials still require a verified TLS transport.</summary>
public sealed class FogNodePolicy
{
    private readonly object gate = new();
    private readonly TimeProvider time;
    private IReadOnlyDictionary<string, FogNodeEnrollment> nodes;
    private ulong epoch;

    public FogNodePolicy(ulong epoch, IEnumerable<FogNodeEnrollment> nodes, TimeProvider? time = null)
    {
        if (epoch == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch));
        }

        this.epoch = epoch;
        this.nodes = Validate(nodes);
        this.time = time ?? TimeProvider.System;
    }

    public ulong Epoch
    {
        get
        {
            lock (gate)
            {
                return epoch;
            }
        }
    }

    public static string SpkiPin(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public void Replace(ulong nextEpoch, IEnumerable<FogNodeEnrollment> nextNodes)
    {
        var validated = Validate(nextNodes);
        lock (gate)
        {
            if (nextEpoch <= epoch)
            {
                throw new ArgumentOutOfRangeException(nameof(nextEpoch));
            }

            nodes = validated;
            epoch = nextEpoch;
        }
    }

    public FogPrincipal Attach(string node, X509Certificate2? certificate)
    {
        lock (gate)
        {
            if (!nodes.TryGetValue(node, out var enrolled) || !enrolled.Enabled || certificate is null ||
                !CertificateMatches(certificate, enrolled))
            {
                throw new FogException("denied");
            }

            return new FogPrincipal(node, enrolled.Boot, epoch, enrolled.SpkiSha256);
        }
    }

    public void Check(FogPrincipal principal, X509Certificate2? certificate)
    {
        lock (gate)
        {
            if (principal.PolicyEpoch != epoch || !nodes.TryGetValue(principal.Node, out var node) || !node.Enabled ||
                node.Boot != principal.Boot || node.SpkiSha256 != principal.SpkiSha256 || certificate is null ||
                !CertificateMatches(certificate, node))
            {
                throw new FogException("denied");
            }
        }
    }

    public bool IsCurrentOwner(string owner)
    {
        lock (gate)
        {
            return nodes.Values.Any(node => node.Enabled && new FogPrincipal(node.Node, node.Boot, epoch, node.SpkiSha256).Owner == owner);
        }
    }

    public bool AuthenticateCertificate(X509Certificate2 certificate)
    {
        lock (gate)
        {
            return nodes.Values.Any(node => node.Enabled && CertificateMatches(certificate, node));
        }
    }

    private static IReadOnlyDictionary<string, FogNodeEnrollment> Validate(IEnumerable<FogNodeEnrollment> enrollments)
    {
        ArgumentNullException.ThrowIfNull(enrollments);
        var result = new Dictionary<string, FogNodeEnrollment>(StringComparer.Ordinal);
        var pins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in enrollments)
        {
            if (!Identifier(node.Node) || !Hex(node.Boot) || !Hex(node.SpkiSha256) ||
                string.IsNullOrEmpty(node.TlsName) || node.TlsName.Length > 253 ||
                !result.TryAdd(node.Node, node) || !pins.Add(node.SpkiSha256))
            {
                throw new ArgumentException("Invalid node enrollment.");
            }
        }

        return new ReadOnlyDictionary<string, FogNodeEnrollment>(result);
    }

    private static bool Identifier(string value) => value is { Length: > 0 and <= 64 } && value is not ("." or "..") &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static bool Hex(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool CertificateMatches(X509Certificate2 certificate, FogNodeEnrollment node) =>
        certificate.NotBefore.ToUniversalTime() <= time.GetUtcNow().UtcDateTime &&
        time.GetUtcNow().UtcDateTime < certificate.NotAfter.ToUniversalTime() && SpkiPin(certificate) == node.SpkiSha256 &&
        certificate.MatchesHostname(node.TlsName, allowWildcards: false, allowCommonName: false);
}
