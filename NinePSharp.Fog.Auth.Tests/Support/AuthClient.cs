using System.Net;
using System.Net.Sockets;
using System.Text;
using Dp9ik;

namespace NinePSharp.Fog.Auth.Tests.Support;

/// <summary>
/// The client side of the auth protocol as 9front's libauthsrv speaks it (_asrequest, _asgetpakkey,
/// _asgetticket), built on the Dp9ik package.
/// </summary>
internal sealed class AuthClient : IDisposable
{
    internal const byte AuthOK = 4;
    internal const byte AuthErr = 5;
    private const int ErrorLength = 64;
    private readonly TcpClient client;
    private readonly NetworkStream stream;

    private AuthClient(TcpClient client)
    {
        this.client = client;
        stream = client.GetStream();
    }

    public void Dispose() => client.Dispose();

    internal static async Task<AuthClient> ConnectAsync(IPEndPoint endPoint)
    {
        var client = new TcpClient();
        await client.ConnectAsync(endPoint);
        return new AuthClient(client);
    }

    internal static TicketRequest Request(AuthMessageType type, string authid, string hostid, string uid, byte[]? challenge = null)
    {
        var request = new TicketRequest(type);
        request.SetAuthId(authid);
        request.SetAuthDomain("fog.example");
        request.SetHostId(hostid);
        request.SetUserId(uid);
        request.SetChallenge(challenge ?? RandomBytes(Dp9ikConstants.ChallengeLength));
        return request;
    }

    internal static AuthKey Key(string password, string id)
    {
        AuthKey key = AuthKey.FromPassword(password);
        key.ApplyAuthPakHash(id);
        return key;
    }

    internal Task SendAsync(TicketRequest request) => SendAsync(request.Marshal());

    internal async Task SendAsync(byte[] bytes)
    {
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    internal void StopSending() => client.Client.Shutdown(SocketShutdown.Send);

    internal async Task<byte> ReadByteAsync() => (await ReadAsync(1))[0];

    internal async Task<string> ReadErrorAsync()
    {
        byte[] error = await ReadAsync(ErrorLength);
        int end = Array.IndexOf(error, (byte)0);
        return Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end);
    }

    internal async Task<AuthKey> PakAsync(AuthKey key)
    {
        byte[] serverValue = await ReadAsync(Dp9ikConstants.PakPublicValueLength);
        var state = new AuthPakState();
        await SendAsync(state.CreatePublicValue(key, isClient: true));
        state.Finish(key, serverValue);
        return key;
    }

    internal Task<byte[]> ReadTicketsAsync() => ReadAsync(2 * Dp9ikConstants.MaxTicketLength);

    internal async Task<byte[]> ReadAsync(int length)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, deadline.Token);
        return buffer;
    }

    internal async Task<int> DrainUntilClosedAsync(TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        var buffer = new byte[512];
        int total = 0;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cancel.Token);
            }
            catch (IOException)
            {
                return total;
            }

            if (read == 0)
            {
                return total;
            }

            total += read;
        }
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
