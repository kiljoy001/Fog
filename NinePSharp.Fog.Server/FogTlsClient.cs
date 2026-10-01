using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace NinePSharp.Fog.Server;

public static class FogTlsClient
{
    /// <summary>Connects only to a numeric endpoint, with an out-of-band server pin and DNS identity.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "On success ownership transfers through TcpClient.GetStream (ownsSocket) to the returned SslStream; all failure paths dispose it.")]
    public static async Task<SslStream> ConnectAsync(IPEndPoint endpoint, string tlsName, string serverSpkiSha256,
        X509Certificate2 nodeCertificate, CancellationToken cancellationToken)
        => await ConnectAsync(endpoint, tlsName, serverSpkiSha256, nodeCertificate, cancellationToken, CreateConnection);

    internal static async Task<SslStream> ConnectAsync(IPEndPoint endpoint, string tlsName, string serverSpkiSha256,
        X509Certificate2 nodeCertificate, CancellationToken cancellationToken, Func<AddressFamily, TcpClient> createConnection)
    {
        if (!nodeCertificate.HasPrivateKey || string.IsNullOrWhiteSpace(tlsName) ||
            serverSpkiSha256.Length != 64 || serverSpkiSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid pinned TLS client configuration.");
        var connection = createConnection(endpoint.AddressFamily);
        try
        {
            await connection.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken);
            var tls = new SslStream(connection.GetStream(), false, (_, peer, _, _) =>
                AcceptServerCertificate(peer, tlsName, serverSpkiSha256, DateTime.UtcNow));
            await tls.AuthenticateAsClientAsync(CreateAuthenticationOptions(tlsName, nodeCertificate), cancellationToken);
            // TcpClient.GetStream owns its socket; SslStream owns that NetworkStream.
            return tls;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static TcpClient CreateConnection(AddressFamily addressFamily) => new(addressFamily) { NoDelay = true };

    /// <summary>A server that presents no certificate, or a non-X509 one, is never accepted.</summary>
    internal static bool AcceptServerCertificate(X509Certificate? peer, string tlsName, string serverSpkiSha256, DateTime now) =>
        peer is X509Certificate2 supplied && ValidateServerCertificate(supplied, tlsName, serverSpkiSha256, now);

    internal static bool ValidateServerCertificate(X509Certificate2 supplied, string tlsName, string serverSpkiSha256, DateTime now) =>
        supplied.MatchesHostname(tlsName, allowWildcards: false, allowCommonName: false) &&
        supplied.NotBefore.ToUniversalTime() <= now && now < supplied.NotAfter.ToUniversalTime() &&
        FogNodePolicy.SpkiPin(supplied) == serverSpkiSha256;

    internal static SslClientAuthenticationOptions CreateAuthenticationOptions(string tlsName, X509Certificate2 nodeCertificate) => new()
    {
        TargetHost = tlsName,
        ClientCertificates = new X509CertificateCollection { nodeCertificate },
        EnabledSslProtocols = SslProtocols.Tls13,
        AllowRenegotiation = false,
        AllowTlsResume = false,
    };
}
