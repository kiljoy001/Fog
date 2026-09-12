using System.Net;
using System.Net.Security;
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
    {
        if (!nodeCertificate.HasPrivateKey || string.IsNullOrWhiteSpace(tlsName) ||
            serverSpkiSha256.Length != 64 || serverSpkiSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid pinned TLS client configuration.");
        var connection = new System.Net.Sockets.TcpClient(endpoint.AddressFamily) { NoDelay = true };
        try
        {
            await connection.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).ConfigureAwait(false);
            var tls = new SslStream(connection.GetStream(), false, (_, peer, _, _) =>
                peer is X509Certificate2 supplied && supplied.MatchesHostname(tlsName, allowWildcards: false, allowCommonName: false) &&
                supplied.NotBefore.ToUniversalTime() <= DateTime.UtcNow && DateTime.UtcNow < supplied.NotAfter.ToUniversalTime() &&
                FogNodePolicy.SpkiPin(supplied) == serverSpkiSha256);
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = tlsName,
                    ClientCertificates = new X509CertificateCollection { nodeCertificate },
                    EnabledSslProtocols = SslProtocols.Tls13,
                    AllowRenegotiation = false,
                    AllowTlsResume = false,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await tls.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            // TcpClient.GetStream owns its socket; SslStream owns that NetworkStream.
            return tls;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
