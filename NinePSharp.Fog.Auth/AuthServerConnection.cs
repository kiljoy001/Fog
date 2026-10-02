using System.Text;
using Dp9ik;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// One connection of 9front's authsrv run with -N (sys/src/cmd/auth/authsrv.c): ticket requests in a
/// loop, AuthPAK filling the authid, hostid and uid key slots, AuthTreq and AuthPass using them once.
/// Any other request, a malformed exchange or the end of the stream ends the connection.
/// </summary>
internal sealed class AuthServerConnection
{
    internal const byte AuthOK = 4;
    internal const byte AuthErr = 5;

    private const int ErrorLength = 64;

    // 9front's list also has "login", "guest" and "passwd", which the length check refuses first.
    private static readonly string[] TrivialPasswords = ["change me", "no passwd", "anonymous"];
    private static readonly Org.BouncyCastle.Security.SecureRandom Random = new();
    private readonly Stream stream;
    private readonly IAuthDatabase database;
    private readonly string remoteAddress;
    private readonly IReadOnlyList<SpeaksForRule> speaksFor;
    private Slot? authKey;
    private Slot? hostKey;
    private Slot? userKey;

    internal AuthServerConnection(Stream stream, IAuthDatabase database, string remoteAddress, IReadOnlyList<SpeaksForRule> speaksFor)
    {
        this.stream = stream;
        this.database = database;
        this.remoteAddress = remoteAddress;
        this.speaksFor = speaksFor;
    }

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
                AuthMessageType.AuthPass => await ChangePasswordAsync(request, cancellationToken),
                _ => false,
            };
            if (!goOn)
            {
                return;
            }

            if (request.Type != AuthMessageType.AuthPak)
            {
                // PAK keys serve one request.
                (authKey, hostKey, userKey) = (null, null, null);
            }
        }
    }

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

    // okpasswd: trailing spaces do not count, and a trivial password is refused read either way round.
    private static string? WeakPassword(string password)
    {
        string trimmed = password.TrimEnd(' ');
        if (trimmed.Length < 8)
        {
            return "password must be at least 8 chars";
        }

        string reversed = new(trimmed.Reverse().ToArray());
        return TrivialPasswords.Contains(trimmed) || TrivialPasswords.Contains(reversed) ? "trivial password" : null;
    }

    private async Task<bool> PakAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        await WriteAsync([AuthOK], cancellationToken);
        (authKey, hostKey, userKey) = (null, null, null);
        if (request.HostIdText.Length > 0)
        {
            if (request.AuthIdText.Length > 0 && (authKey = await PakAsync(request.AuthIdText, cancellationToken)) is null)
            {
                return false;
            }

            return (hostKey = await PakAsync(request.HostIdText, cancellationToken)) is not null;
        }

        return request.UserIdText.Length == 0 || (userKey = await PakAsync(request.UserIdText, cancellationToken)) is not null;
    }

    private async Task<Slot?> PakAsync(string id, CancellationToken cancellationToken)
    {
        AuthKey? found = database.FindKey(id);
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

    private async Task<bool> TicketRequestAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        if (request.UserIdText.Length == 0)
        {
            return false;
        }

        if (await GetKeyAsync(request.AuthIdText, authKey, cancellationToken) is not AuthKey server)
        {
            return false;
        }

        if (await GetKeyAsync(request.HostIdText, hostKey, cancellationToken) is not AuthKey host)
        {
            return false;
        }

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

    private async Task<AuthKey?> GetKeyAsync(string id, Slot? slot, CancellationToken cancellationToken)
    {
        if (id.Length == 0)
        {
            return null;
        }

        if (slot?.Id == id)
        {
            return slot.Key;
        }

        await RefuseAsync("DES is disabled", cancellationToken);
        return null;
    }

    private async Task<bool> ChangePasswordAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        string user = request.UserIdText;
        if (await GetKeyAsync(user, userKey, cancellationToken) is not AuthKey current)
        {
            return false;
        }

        var ticket = new Ticket(AuthMessageType.AuthTp, TicketEncryptionForm.Form1);
        ticket.SetChallenge(request.Challenge);
        ticket.SetClientUser(user);
        ticket.SetServerUser(user);
        ticket.SetSessionKey(RandomBytes(Dp9ikConstants.NonceLength));
        await WriteAsync([AuthOK, .. ticket.Marshal(current)], cancellationToken);
        while (true)
        {
            byte[] sealedRequest = await ReadAsync(Dp9ikConstants.MaxPasswordRequestLength, cancellationToken);
            if (!PasswordRequest.TryUnmarshal(ticket, sealedRequest, out PasswordRequest? change, out _) || change!.Type != AuthMessageType.AuthPass)
            {
                await RefuseAsync($"protocol botch1: {remoteAddress}", cancellationToken);
                return false;
            }

            if (await TryChangeAsync(user, current, change, cancellationToken))
            {
                break;
            }
        }

        database.Succeed(user);
        await WriteAsync([AuthOK], cancellationToken);
        return true;
    }

    private async Task<bool> TryChangeAsync(string user, AuthKey current, PasswordRequest change, CancellationToken cancellationToken)
    {
        AuthKey old = AuthKey.FromPassword(change.OldPasswordText);
        string? refusal = null;
        if (!old.DesKey.AsSpan().SequenceEqual(current.DesKey))
        {
            refusal = "protocol botch2:";
        }
        else if (current.AesKey.Any(value => value != 0) && !old.AesKey.AsSpan().SequenceEqual(current.AesKey))
        {
            refusal = "protocol botch3:";
        }
        else if (change.NewPasswordText.Length > 0 && WeakPassword(change.NewPasswordText) is string weak)
        {
            refusal = weak;
        }
        else if (change.ChangeSecret && !database.SetSecret(user, change.SecretText))
        {
            refusal = "can't write secret";
        }
        else if (change.NewPasswordText.Length > 0 && !database.SetKey(user, AuthKey.FromPassword(change.NewPasswordText)))
        {
            refusal = "can't write key";
        }

        if (refusal is null)
        {
            return true;
        }

        await RefuseAsync($"{refusal} {remoteAddress}", cancellationToken);
        return false;
    }

    private bool SpeaksFor(string speaker, string user)
    {
        if (speaker == user)
        {
            return true;
        }

        bool speaks = false;
        foreach (SpeaksForRule rule in speaksFor.Where(rule => rule.HostId == speaker))
        {
            if (rule.Uid == "!" + user)
            {
                return false;
            }

            if (rule.Uid == "*" || rule.Uid == user)
            {
                speaks = true;
            }
        }

        return speaks;
    }

    private async Task RefuseAsync(string message, CancellationToken cancellationToken)
    {
        var reply = new byte[1 + ErrorLength];
        reply[0] = AuthErr;
        Encoding.UTF8.GetBytes(message).CopyTo(reply, 1);
        await WriteAsync(reply, cancellationToken);
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
