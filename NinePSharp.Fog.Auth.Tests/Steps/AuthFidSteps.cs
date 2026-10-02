using System.Globalization;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Dp9ik;
using NinePSharp.Constants;
using NinePSharp.Fog.Auth.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Parser;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests.Steps;

[Binding]
[Scope(Feature = "A 9P export authenticates attaches with dp9ik as 9front's lib9p and factotum do")]
public sealed class AuthFidSteps
{
    private const string Session = "connection-1";
    private const string OtherSession = "connection-2";
    private static readonly BigInteger P = BigInteger.Pow(2, 448) - BigInteger.Pow(2, 224) - 1;
    private readonly Dictionary<string, string> passwords = new(StringComparer.Ordinal);
    private readonly List<object> replies = new();
    private readonly RecordingExport inner = new();
    private SoftwareTpm? tpm;
    private string directory = string.Empty;
    private KeyFsHost? keyfsHost;
    private KeyFsClient? admin;
    private AuthServerHost? server;
    private AuthFidExport? export;
    private ushort tag;
    private byte[] clientChallenge = [];
    private TicketRequest? pakRequest;
    private byte[] serverPakValue = [];
    private Ticket? clientTicket;
    private byte[] serverTicket = [];
    private byte[] authenticator = [];
    private AuthKey? serverKey;
    private byte[]? clientTicketBytes;
    private AuthKey? clientKey;

    private object Last => replies[^1];

    [Given("a keyfs with the users")]
    public async Task GivenKeyFs(Table table)
    {
        tpm = SoftwareTpm.Start();
        directory = Directory.CreateTempSubdirectory("keyfs-").FullName;
        var options = new KeyFsOptions
        {
            StateDirectory = directory,
            SocketPath = Path.Combine(directory, "keys.sock"),
            OpenTpm = tpm.OpenDevice,
        };
        keyfsHost = await KeyFsHost.StartAsync(options, CancellationToken.None);
        admin = await KeyFsClient.AttachAsync(options.SocketPath);
        foreach (DataTableRow row in table.Rows)
        {
            AuthKey key = AuthKey.FromPassword(row["password"]);
            await admin.CreateAsync(string.Empty, row["user"], isDirectory: true);
            await admin.WriteAsync($"{row["user"]}/key", key.DesKey);
            await admin.WriteAsync($"{row["user"]}/aeskey", key.AesKey);
            passwords[row["user"]] = row["password"];
        }
    }

    [Given("an auth server on that keyfs")]
    public void GivenServer()
        => server = AuthServerHost.Start(new AuthServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, keyfsHost!);

    [Given(@"^an export that authenticates as ""(.*)"" in the auth domain ""(.*)""$")]
    public async Task GivenExport(string authId, string authDomain)
    {
        export = new AuthFidExport(inner, keyfsHost!, new AuthFidOptions { AuthId = authId, AuthDomain = authDomain });
        inner.Auth = export;
        Assert.IsType<Rversion>(await SendAsync(Session, NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000"))));
    }

    [Given(@"^the keyfs user ""(.*)"" is (unknown|disabled|expired)$")]
    public async Task GivenUserState(string user, string state)
    {
        switch (state)
        {
            case "unknown":
                await admin!.RemoveAsync(user);
                break;
            case "disabled":
                await admin!.WriteTextAsync($"{user}/status", "disabled");
                break;
            default:
                await admin!.WriteTextAsync($"{user}/expire", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100).ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    [Given(@"^the client sent Tauth for afid (\d+) as ""(.*)"" to ""(.*)""$")]
    [When(@"^the client sends Tauth for afid (\d+) as ""(.*)"" to ""(.*)""$")]
    public Task WhenTauth(uint afid, string uname, string aname)
        => SendAsync(Session, NinePMessage.NewMsgTauth(new Tauth(NextTag(), afid, uname, aname)));

    [Given(@"^the client sent Tauth for afid (\d+) as ""(.*)"" to ""(.*)"" and read the offer$")]
    public async Task GivenTauthAndOffer(uint afid, string uname, string aname)
    {
        await WhenTauth(afid, uname, aname);
        Assert.IsType<Rauth>(Last);
        Assert.IsType<Rread>(await ReadAsync(afid, 128));
    }

    [Given(@"^the client read (\d+) bytes from afid (\d+)$")]
    [When(@"^the client reads (\d+) bytes from afid (\d+)$")]
    public Task WhenRead(uint count, uint afid) => ReadAsync(afid, count);

    [Given(@"^the client chose ""(.*)"" on afid (\d+)$")]
    public async Task GivenChoice(string choice, uint afid)
        => Assert.IsType<Rwrite>(await WriteAsync(afid, Encoding.UTF8.GetBytes(choice + "\0")));

    [When(@"^the client writes ""(.*)"" and a NUL byte to afid (\d+)$")]
    public Task WhenWriteWithNul(string text, uint afid) => WriteAsync(afid, Encoding.UTF8.GetBytes(text + "\0"));

    [When(@"^the client writes ""(.*)"" without a NUL byte to afid (\d+)$")]
    [When(@"^the client writes ""(.*)"" to afid (\d+)$")]
    public Task WhenWriteText(string text, uint afid) => WriteAsync(afid, Encoding.UTF8.GetBytes(text));

    [When(@"^the client writes a challenge to afid (\d+) and reads the PAK request$")]
    public Task WhenChallengeAndPakRequest(uint afid) => ChallengeAndPakRequestAsync(afid);

    [When(@"^the client writes a 7-byte challenge to afid (\d+)$")]
    public Task WhenShortChallenge(uint afid) => WriteAsync(afid, new byte[7]);

    [When(@"^the client writes a challenge, reads the PAK request and writes 55 bytes$")]
    public async Task WhenShortPakValue()
    {
        await ChallengeAndPakRequestAsync(1);
        await WriteAsync(1, new byte[Dp9ikConstants.PakPublicValueLength - 1]);
    }

    [When(@"^the client writes a challenge, reads the PAK request and writes an encoding greater than \(p-1\)/2$")]
    public async Task WhenInvalidPakValue()
    {
        await ChallengeAndPakRequestAsync(1);
        await WriteAsync(1, (P - 1).ToByteArray(isUnsigned: true, isBigEndian: true));
    }

    [Then(@"^the request is an AuthPAK ticket request from ""(.*)"" in ""(.*)"" with no host or user id$")]
    public void ThenPakRequest(string authId, string authDomain)
    {
        Assert.Equal(AuthMessageType.AuthPak, pakRequest!.Type);
        Assert.Equal(authId, pakRequest.AuthIdText);
        Assert.Equal(authDomain, pakRequest.AuthDomainText);
        Assert.Equal(string.Empty, pakRequest.HostIdText);
        Assert.Equal(string.Empty, pakRequest.UserIdText);
    }

    [Then(@"^a (\d+)-byte PAK public value follows it$")]
    public void ThenPakValue(int length) => Assert.Equal(length, serverPakValue.Length);

    [When(@"^""(.*)"" authenticates afid (\d+) for ""([^""]*)"" with password ""(.*)""$")]
    [Given(@"^""(.*)"" authenticated afid (\d+) for ""([^""]*)"" with password ""(.*)""$")]
    public Task WhenAuthenticate(string user, uint afid, string aname, string password)
        => AuthenticateAsync(user, afid, aname, user, password);

    [When(@"^""(.*)"" authenticates afid (\d+) for ""(.*)"" as the uname ""(.*)"" with password ""(.*)""$")]
    [Given(@"^""(.*)"" authenticated afid (\d+) for ""(.*)"" as the uname ""(.*)"" with password ""(.*)""$")]
    public Task WhenAuthenticateAs(string user, uint afid, string aname, string uname, string password)
        => AuthenticateAsync(user, afid, aname, uname, password);

    [Given(@"^""(.*)"" has run dp9ik on afid (\d+) as far as the ticket with password ""(.*)""$")]
    public async Task GivenAsFarAsTicket(string user, uint afid, string password)
    {
        await TicketsAsync(user, afid, "/", user, password);
        Assert.NotNull(clientTicket);
    }

    [When(@"^""(.*)"" tries dp9ik on afid (\d+) with password ""(.*)""$")]
    public Task WhenTry(string user, uint afid, string password) => TicketsAsync(user, afid, "/", user, password);

    [Then("the client cannot open its ticket")]
    public void ThenCannotOpen()
    {
        Assert.False(Ticket.TryUnmarshal(clientKey!, clientTicketBytes!, out Ticket? opened, out _) && opened!.Type == AuthMessageType.AuthTc);
        Assert.Null(clientTicket);
    }

    [When(@"^the client writes its ticket and authenticator to afid (\d+)$")]
    public Task WhenWriteTicket(uint afid) => WriteAsync(afid, [.. serverTicket, .. authenticator]);

    [When(@"^the client writes a ticket sealed with another key to afid (\d+)$")]
    public Task WhenWriteForeignTicket(uint afid)
    {
        AuthKey other = AuthKey.CreateRandom();
        var forged = new Ticket(AuthMessageType.AuthTs, TicketEncryptionForm.Form1);
        forged.SetChallenge(pakRequest!.Challenge);
        forged.SetClientUser("glenda");
        forged.SetServerUser("glenda");
        forged.SetSessionKey(clientTicket!.SessionKey);
        return WriteAsync(afid, [.. forged.Marshal(Shared(other)), .. authenticator]);
    }

    [When(@"^the client writes its ticket with an authenticator for another challenge to afid (\d+)$")]
    public Task WhenWriteOtherChallenge(uint afid)
        => WriteAsync(afid, [.. serverTicket, .. MakeAuthenticator(clientTicket!, AuthMessageType.AuthAc, OtherChallenge(pakRequest!.Challenge))]);

    [When(@"^the client writes its ticket truncated to (\d+) bytes to afid (\d+)$")]
    public Task WhenWriteTruncated(int length, uint afid) => WriteAsync(afid, serverTicket[..length]);

    [When(@"^the client reads the server's authenticator from afid (\d+)$")]
    public Task WhenReadAuthenticator(uint afid) => ReadAsync(afid, 128);

    [Then("the server's authenticator answers the client's challenge")]
    public void ThenServerAuthenticator()
    {
        Rread read = Assert.IsType<Rread>(Last);
        Assert.True(Authenticator.TryUnmarshal(clientTicket!, read.Data.Span, out Authenticator? answer, out _));
        Assert.Equal(AuthMessageType.AuthAs, answer!.Type);
        Assert.Equal(clientChallenge, answer.Challenge);
        Assert.Contains(answer.Random, value => value != 0);
    }

    [Given(@"^the client plays the auth server for ""(.*)"" with password ""(.*)"" on afid (\d+) as ""(.*)""$")]
    public async Task GivenPlayAuthServer(string authId, string password, uint afid, string uname)
    {
        await GivenTauthAndOffer(afid, uname, "/");
        await GivenChoice("dp9ik fog.example", afid);
        await ChallengeAndPakRequestAsync(afid);
        serverKey = AuthClient.Key(password, authId);
        var state = new AuthPakState();
        byte[] value = state.CreatePublicValue(serverKey, isClient: false);
        state.Finish(serverKey, serverPakValue);
        Assert.IsType<Rwrite>(await WriteAsync(afid, value));
    }

    [When(@"^the client writes a form (0|1) (AuthTs|AuthTc) ticket for ""(.*)"" and an (AuthAc|AuthAs) authenticator to afid (\d+)$")]
    public Task WhenWriteMadeTicket(int form, string type, string user, string authenticatorType, uint afid)
        => WriteMadeTicketAsync(afid, form, Enum.Parse<AuthMessageType>(type), user, false, Enum.Parse<AuthMessageType>(authenticatorType), false);

    [When(@"^the client writes a form 1 AuthTs ticket for another challenge to afid (\d+)$")]
    public Task WhenWriteTicketOtherChallenge(uint afid)
        => WriteMadeTicketAsync(afid, 1, AuthMessageType.AuthTs, "glenda", true, AuthMessageType.AuthAc, false);

    [When(@"^the client writes a form 1 AuthTs ticket and an AuthAs authenticator to afid (\d+)$")]
    public Task WhenWriteAuthAs(uint afid)
        => WriteMadeTicketAsync(afid, 1, AuthMessageType.AuthTs, "glenda", false, AuthMessageType.AuthAs, false);

    [When(@"^the client writes a form 1 AuthTs ticket and an authenticator for another challenge to afid (\d+)$")]
    public Task WhenWriteAuthenticatorOtherChallenge(uint afid)
        => WriteMadeTicketAsync(afid, 1, AuthMessageType.AuthTs, "glenda", false, AuthMessageType.AuthAc, true);

    [When(@"^the client attaches fid (\d+) with afid (\d+) as ""(.*)"" to ""(.*)""$")]
    public Task WhenAttach(uint fid, uint afid, string uname, string aname) => AttachAsync(Session, fid, afid, uname, aname);

    [When(@"^the client attaches fid (\d+) without an afid as ""(.*)"" to ""(.*)""$")]
    public Task WhenAttachWithoutAfid(uint fid, string uname, string aname) => AttachAsync(Session, fid, NinePConstants.NoFid, uname, aname);

    [Given(@"^the client has an attached fid (\d+) without an afid$")]
    public async Task GivenAttachedFid(uint fid)
    {
        await AttachAsync(Session, fid, NinePConstants.NoFid, "node-1", "/");
        Assert.IsType<Rattach>(Last);
    }

    [When(@"^another connection attaches fid (\d+) with afid (\d+) as ""(.*)"" to ""(.*)""$")]
    public async Task WhenOtherAttach(uint fid, uint afid, string uname, string aname)
    {
        Assert.IsType<Rversion>(await SendAsync(OtherSession, NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000"))));
        await AttachAsync(OtherSession, fid, afid, uname, aname);
    }

    [When("the client sends Tversion again")]
    public Task WhenVersion() => SendAsync(Session, NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000")));

    [When(@"^the client clunks fid (\d+)$")]
    public Task WhenClunk(uint fid) => SendAsync(Session, NinePMessage.NewMsgTclunk(new Tclunk(NextTag(), fid)));

    [When(@"^the client walks fid (\d+) to fid (\d+)$")]
    public Task WhenWalk(uint fid, uint newFid) => SendAsync(Session, NinePMessage.NewMsgTwalk(new Twalk(NextTag(), fid, newFid, [])));

    [When(@"^the client walks fid (\d+) to fid (\d+) through ""(.*)""$")]
    public Task WhenWalkThrough(uint fid, uint newFid, string path)
        => SendAsync(Session, NinePMessage.NewMsgTwalk(new Twalk(NextTag(), fid, newFid, path.Split('/'))));

    [When(@"^the client opens fid (\d+)$")]
    public Task WhenOpen(uint fid) => SendAsync(Session, NinePMessage.NewMsgTopen(new Topen(NextTag(), fid, 0)));

    [When(@"^the client creates ""(.*)"" in fid (\d+)$")]
    public Task WhenCreate(string name, uint fid) => SendAsync(Session, NinePMessage.NewMsgTcreate(new Tcreate(NextTag(), fid, name, 0x1b6, 0)));

    [When(@"^the client stats fid (\d+)$")]
    public Task WhenStat(uint fid) => SendAsync(Session, NinePMessage.NewMsgTstat(new Tstat(NextTag(), fid)));

    [When(@"^the client wstats fid (\d+)$")]
    public Task WhenWstat(uint fid)
    {
        var stat = new Stat(0, ushort.MaxValue, uint.MaxValue, new Qid((QidType)0xff, uint.MaxValue, ulong.MaxValue), uint.MaxValue, uint.MaxValue, uint.MaxValue, ulong.MaxValue, string.Empty, string.Empty, string.Empty, string.Empty);
        return SendAsync(Session, NinePMessage.NewMsgTwstat(new Twstat(NextTag(), fid, stat)));
    }

    [When(@"^the client removes fid (\d+)$")]
    public Task WhenRemove(uint fid) => SendAsync(Session, NinePMessage.NewMsgTremove(new Tremove(NextTag(), fid)));

    [Then("the reply is Rauth with a QTAUTH qid")]
    public void ThenRauth() => Assert.Equal(QidType.QTAUTH, Assert.IsType<Rauth>(Last).Aqid.Type);

    [Then(@"^the reply is Rerror ""(.*)""$")]
    public void ThenError(string message) => Assert.Equal(message, Assert.IsType<Rerror>(Last).Ename);

    [Then("the reply is Rauth")]
    public void ThenRauthOnly() => Assert.IsType<Rauth>(Last);

    [When(@"^the client writes (\d+) bytes beginning ""(.*)"" to afid (\d+)$")]
    public Task WhenWriteBeginning(int length, string prefix, uint afid)
    {
        var data = new byte[length];
        Encoding.ASCII.GetBytes(prefix).CopyTo(data, 0);
        return WriteAsync(afid, data);
    }

    [When("the connection closes")]
    public Task WhenClose() => export!.CloseSessionAsync(Session);

    [Then("the export underneath saw the connection close")]
    public void ThenInnerClosed() => Assert.Equal([Session], inner.Closed);

    [Then("the reply is Rwrite")]
    public void ThenRwrite() => Assert.IsType<Rwrite>(Last);

    [Then(@"^it reads ""(.*)"" and a NUL byte$")]
    public void ThenReadsText(string text) => Assert.Equal(Encoding.UTF8.GetBytes(text + "\0"), Assert.IsType<Rread>(Last).Data.ToArray());

    [Then(@"^it reads (\d+) bytes$")]
    public void ThenReadsCount(int count) => Assert.Equal(count, Assert.IsType<Rread>(Last).Data.Length);

    [Then(@"^the attach succeeds as the user ""(.*)""$")]
    public void ThenAttachedAs(string user)
    {
        Assert.IsType<Rattach>(Last);
        Assert.Equal(user, inner.Attaches[^1].User);
    }

    [Then(@"^both attaches succeed as the user ""(.*)""$")]
    public void ThenBothAttached(string user)
    {
        Assert.All(replies[^2..], reply => Assert.IsType<Rattach>(reply));
        Assert.Equal([(0u, user), (2u, user)], inner.Attaches.Select(attach => (attach.Fid, attach.User)));
    }

    [Then("the attach succeeds without an authenticated user")]
    public void ThenAttachedWithoutUser()
    {
        Assert.IsType<Rattach>(Last);
        Assert.Null(inner.Attaches[^1].User);
    }

    [AfterScenario]
    public async Task CleanUpAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync();
        }

        admin?.Dispose();
        if (keyfsHost is not null)
        {
            await keyfsHost.DisposeAsync();
        }

        tpm?.Dispose();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] OtherChallenge(byte[] challenge) => challenge.Select(value => (byte)~value).ToArray();

    private static byte[] MakeAuthenticator(Ticket ticket, AuthMessageType type, byte[] challenge)
    {
        var made = new Authenticator(type);
        made.SetChallenge(challenge);
        made.SetRandom(RandomNumberGenerator.GetBytes(Dp9ikConstants.NonceLength));
        return made.Marshal(ticket);
    }

    // A key whose PAK key is set, from an exchange with itself.
    private static AuthKey Shared(AuthKey key)
    {
        key.ApplyAuthPakHash("other");
        var client = new AuthPakState();
        client.CreatePublicValue(key, isClient: true);
        client.Finish(key, new AuthPakState().CreatePublicValue(key, isClient: false));
        return key;
    }

    private ushort NextTag() => tag++;

    private async Task<object> SendAsync(string session, NinePMessage message)
    {
        object reply = await export!.DispatchAsync(session, message, NinePDialect.NineP2000);
        replies.Add(reply);
        return reply;
    }

    private Task<object> ReadAsync(uint afid, uint count)
        => SendAsync(Session, NinePMessage.NewMsgTread(new Tread(NextTag(), afid, 0, count)));

    private Task<object> WriteAsync(uint afid, byte[] data)
        => SendAsync(Session, NinePMessage.NewMsgTwrite(new Twrite(NextTag(), afid, 0, data)));

    private Task<object> AttachAsync(string session, uint fid, uint afid, string uname, string aname)
        => SendAsync(session, NinePMessage.NewMsgTattach(new Tattach(NextTag(), fid, afid, uname, aname)));

    private async Task ChallengeAndPakRequestAsync(uint afid)
    {
        clientChallenge = RandomNumberGenerator.GetBytes(Dp9ikConstants.ChallengeLength);
        Assert.IsType<Rwrite>(await WriteAsync(afid, clientChallenge));
        Rread read = Assert.IsType<Rread>(await ReadAsync(afid, 4096));
        Assert.True(TicketRequest.TryUnmarshal(read.Data.Span, out pakRequest, out int consumed));
        serverPakValue = read.Data[consumed..].ToArray();
    }

    // factotum's dp9ik client role: the PAK request goes to the auth server with the client's
    // host and user ids, its AuthTs ticket and an authenticator go back to the afid.
    private async Task TicketsAsync(string user, uint afid, string aname, string uname, string password)
    {
        await GivenTauthAndOffer(afid, uname, aname);
        await GivenChoice("dp9ik fog.example", afid);
        await ChallengeAndPakRequestAsync(afid);
        var request = new TicketRequest(AuthMessageType.AuthPak);
        request.SetAuthId(pakRequest!.AuthIdText);
        request.SetAuthDomain(pakRequest.AuthDomainText);
        request.SetChallenge(pakRequest.Challenge);
        request.SetHostId(user);
        request.SetUserId(user);
        using AuthClient client = await AuthClient.ConnectAsync(server!.LocalEndPoint);
        await client.SendAsync(request);
        await client.SendAsync(serverPakValue);
        clientKey = AuthClient.Key(password, user);
        var state = new AuthPakState();
        await client.SendAsync(state.CreatePublicValue(clientKey, isClient: true));
        Assert.Equal(AuthClient.AuthOK, await client.ReadByteAsync());
        byte[] values = await client.ReadAsync(2 * Dp9ikConstants.PakPublicValueLength);
        state.Finish(clientKey, values.AsSpan(Dp9ikConstants.PakPublicValueLength));
        var ticketRequest = new TicketRequest(AuthMessageType.AuthTreq);
        ticketRequest.SetAuthId(request.AuthIdText);
        ticketRequest.SetAuthDomain(request.AuthDomainText);
        ticketRequest.SetChallenge(request.Challenge);
        ticketRequest.SetHostId(user);
        ticketRequest.SetUserId(user);
        await client.SendAsync(ticketRequest);
        Assert.Equal(AuthClient.AuthOK, await client.ReadByteAsync());
        byte[] tickets = await client.ReadTicketsAsync();
        Assert.IsType<Rwrite>(await WriteAsync(afid, values[..Dp9ikConstants.PakPublicValueLength]));
        clientTicketBytes = tickets[..Dp9ikConstants.MaxTicketLength];
        serverTicket = tickets[Dp9ikConstants.MaxTicketLength..];
        clientTicket = Ticket.TryUnmarshal(clientKey, clientTicketBytes, out Ticket? opened, out _) && opened!.Type == AuthMessageType.AuthTc ? opened : null;
        if (clientTicket is not null)
        {
            authenticator = MakeAuthenticator(clientTicket, AuthMessageType.AuthAc, pakRequest.Challenge);
        }
    }

    private async Task AuthenticateAsync(string user, uint afid, string aname, string uname, string password)
    {
        await TicketsAsync(user, afid, aname, uname, password);
        Assert.IsType<Rwrite>(await WriteAsync(afid, [.. serverTicket, .. authenticator]));
        await WhenReadAuthenticator(afid);
        ThenServerAuthenticator();
    }

    private Task WriteMadeTicketAsync(uint afid, int form, AuthMessageType type, string user, bool otherTicketChallenge, AuthMessageType authenticatorType, bool otherAuthenticatorChallenge)
    {
        var made = new Ticket(type, form == 0 ? TicketEncryptionForm.Form0 : TicketEncryptionForm.Form1);
        made.SetChallenge(otherTicketChallenge ? OtherChallenge(pakRequest!.Challenge) : pakRequest!.Challenge);
        made.SetClientUser(user);
        made.SetServerUser(user);
        made.SetSessionKey(RandomNumberGenerator.GetBytes(Dp9ikConstants.NonceLength));
        clientTicket = made;
        byte[] madeAuthenticator = MakeAuthenticator(made, authenticatorType, otherAuthenticatorChallenge ? OtherChallenge(pakRequest.Challenge) : pakRequest.Challenge);
        return WriteAsync(afid, [.. made.Marshal(serverKey!), .. madeAuthenticator]);
    }
}
