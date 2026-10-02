using System.Text;
using Dp9ik;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// One connection of 9front's authsrv run with -N (sys/src/cmd/auth/authsrv.c): ticket requests in a
/// loop, AuthPAK filling the authid, hostid and uid key slots, and AuthTreq using them once for form 1
/// tickets. Any other request, a malformed exchange or the end of the stream ends the connection.
/// </summary>
internal sealed class AuthServerConnection
{
    internal const byte AuthOK = 4;
    internal const byte AuthErr = 5;

    /// <summary>AERRLEN: an error reply's message field.</summary>
    private const int ErrorLength = 64;

    private static readonly Org.BouncyCastle.Security.SecureRandom Random = new();
    private readonly Stream stream;
    private readonly Func<string, AuthKey?> findKey;
    private readonly IReadOnlyList<SpeaksForRule> speaksFor;
    private Slot? authKey;
    private Slot? hostKey;
    private Slot? userKey;

    internal AuthServerConnection(Stream stream, Func<string, AuthKey?> findKey, IReadOnlyList<SpeaksForRule> speaksFor)
    {
        this.stream = stream;
        this.findKey = findKey;
        this.speaksFor = speaksFor;
    }

    /// <summary>
    /// Serves requests until a request ends the connection. A client that stops sending ends it with
    /// <see cref="EndOfStreamException"/>, and <paramref name="cancellationToken"/> with cancellation.
    /// </summary>
    internal async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[] buffer = await ReadAsync(Dp9ikConstants.TicketRequestLength, cancellationToken);
            TicketRequest.TryUnmarshal(buffer, out TicketRequest? request, out _);
            bool goOn = request!.Type switch
            {
                AuthMessageType.AuthPak => await PakAsync(request, cancellationToken),
                AuthMessageType.AuthTreq => await TicketRequestAsync(request, cancellationToken),
                _ => false,
            };
            if (!goOn) return;
            if (request.Type != AuthMessageType.AuthPak)
            {
                // PAK keys serve one request.
                (authKey, hostKey, userKey) = (null, null, null);
            }
        }
    }

    /// <summary>authsrv's pak: AuthOK, then one AuthPAK exchange for the authid and the hostid, or for the uid alone.</summary>
    private async Task<bool> PakAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        await WriteAsync([AuthOK], cancellationToken);
        (authKey, hostKey, userKey) = (null, null, null);
        if (request.HostIdText.Length > 0)
        {
            if (request.AuthIdText.Length > 0 && (authKey = await PakAsync(request.AuthIdText, cancellationToken)) is null) return false;
            return (hostKey = await PakAsync(request.HostIdText, cancellationToken)) is not null;
        }

        return request.UserIdText.Length == 0 || (userKey = await PakAsync(request.UserIdText, cancellationToken)) is not null;
    }

    /// <summary>
    /// authsrv's pak1: the server's public value, then the client's. An id without a usable AES key
    /// gets a made-up key so the client cannot tell. Null ends the connection.
    /// </summary>
    private async Task<Slot?> PakAsync(string id, CancellationToken cancellationToken)
    {
        AuthKey? found = findKey(id);
        AuthKey key = found is not null && found.AesKey.Any(value => value != 0) ? found : MadeUp(id);
        var state = new AuthPakState();
        await WriteAsync(state.CreatePublicValue(key, isClient: false), cancellationToken);
        byte[] peer = await ReadAsync(Dp9ikConstants.PakPublicValueLength, cancellationToken);
        try
        {
            state.Finish(key, peer);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return new Slot(id, key);
    }

    /// <summary>
    /// authsrv's ticketrequest: an AuthTc ticket under the host's PAK key and an AuthTs ticket under
    /// the authid's, carrying a fresh key. A host that does not speak for the user gets tickets
    /// sealed with made-up keys.
    /// </summary>
    private async Task<bool> TicketRequestAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        if (request.UserIdText.Length == 0) return false;
        if (await GetKeyAsync(request.AuthIdText, cancellationToken) is not AuthKey server) return false;
        if (await GetKeyAsync(request.HostIdText, cancellationToken) is not AuthKey host) return false;
        if (!SpeaksFor(request.HostIdText, request.UserIdText))
        {
            server = MadeUp(request.AuthIdText);
            host = MadeUp(request.HostIdText);
        }

        var ticket = new Ticket(AuthMessageType.AuthTc, TicketEncryptionForm.Form1);
        ticket.SetChallenge(request.Challenge);
        ticket.SetClientUser(request.UserIdText);
        ticket.SetServerUser(request.UserIdText);
        ticket.SetSessionKey(RandomBytes(Dp9ikConstants.NonceLength));
        byte[] clientTicket = ticket.Marshal(host);
        var serverTicket = new Ticket(AuthMessageType.AuthTs, TicketEncryptionForm.Form1);
        serverTicket.SetChallenge(ticket.Challenge);
        serverTicket.SetClientUser(request.UserIdText);
        serverTicket.SetServerUser(request.UserIdText);
        serverTicket.SetSessionKey(ticket.SessionKey);
        await WriteAsync([AuthOK, .. clientTicket, .. serverTicket.Marshal(server)], cancellationToken);
        return true;
    }

    /// <summary>
    /// authsrv's getkey with tickets in form 1: an empty id ends the connection, and only an id with a
    /// PAK key from this connection's last AuthPAK exchange has a key, as DES keys are never used.
    /// Null means the connection ends.
    /// </summary>
    private async Task<AuthKey?> GetKeyAsync(string id, CancellationToken cancellationToken)
    {
        if (id.Length == 0) return null;
        Slot? slot = new[] { authKey, hostKey, userKey }.FirstOrDefault(each => each?.Id == id);
        if (slot is not null) return slot.Key;
        await RefuseAsync("DES is disabled", cancellationToken);
        return null;
    }

    /// <summary>authsrv's speaksfor over the configured /lib/ndb/auth lines.</summary>
    private bool SpeaksFor(string speaker, string user)
    {
        if (speaker == user) return true;
        bool speaks = false;
        foreach (SpeaksForRule rule in speaksFor.Where(rule => rule.HostId == speaker))
        {
            if (rule.Uid == "!" + user) return false;
            if (rule.Uid == "*" || rule.Uid == user) speaks = true;
        }

        return speaks;
    }

    /// <summary>replyerror: AuthErr and the message in AERRLEN bytes.</summary>
    private async Task RefuseAsync(string message, CancellationToken cancellationToken)
    {
        var reply = new byte[1 + ErrorLength];
        reply[0] = AuthErr;
        Encoding.UTF8.GetBytes(message).CopyTo(reply, 1);
        await WriteAsync(reply, cancellationToken);
    }

    /// <summary>authsrv's mkkey and authpak_hash: random keys, so nothing sealed with them can be opened.</summary>
    private static AuthKey MadeUp(string id)
    {
        AuthKey key = AuthKey.CreateRandom();
        key.ApplyAuthPakHash(id);
        return key;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.NextBytes(bytes);
        return bytes;
    }

    private async Task<byte[]> ReadAsync(int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken) => await stream.WriteAsync(bytes, cancellationToken);

    private sealed record Slot(string Id, AuthKey Key);
}
