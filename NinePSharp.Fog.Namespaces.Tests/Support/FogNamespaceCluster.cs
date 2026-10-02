using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace NinePSharp.Fog.Namespaces.Tests.Support;

/// <summary>One two-silo test cluster shared by the feature, as the Orleans namespace tests do.</summary>
internal static class FogNamespaceCluster
{
    private static readonly object Gate = new();
    private static TestCluster? cluster;

    internal static TestCluster Cluster
        => cluster ?? throw new InvalidOperationException("The Orleans test cluster has not been started.");

    internal static void Start()
    {
        lock (Gate)
        {
            if (cluster is not null)
            {
                return;
            }

            var builder = new TestClusterBuilder(2);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            cluster = builder.Build();
            cluster.Deploy();
        }
    }

    internal static void Stop()
    {
        lock (Gate)
        {
            cluster?.StopAllSilos();
            cluster?.Dispose();
            cluster = null;
        }
    }

    internal static X509Certificate2 Certificate(string name)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.AddMemoryGrainStorageAsDefault();
    }
}
