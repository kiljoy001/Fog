using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using Dp9ik;
using NinePSharp.Fog.Auth.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests.Steps;

[Binding]
[Scope(Feature = "Fog issues dp9ik tickets as 9front authsrv does")]
public sealed class AuthSrvSteps
{
    private static readonly Dictionary<string, byte> RequestTypes = new(StringComparer.Ordinal)
    {
        ["AuthChal"] = 2, ["AuthApop"] = 7, ["AuthChap"] = 10, ["AuthMSchap"] = 11, ["AuthCram"] = 12,
        ["AuthVNC"] = 14, ["AuthMSchapv2"] = 21, ["AuthNTLM"] = 22, ["AuthTs"] = 64,
    };

    private static readonly BigInteger P = BigInteger.Pow(2, 448) - BigInteger.Pow(2, 224) - 1;
    private readonly Dictionary<string, string> passwords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthKey> clientKeys = new(StringComparer.Ordinal);
    private readonly List<byte[]> ticketKeys = new();
    private readonly List<AuthClient> others = new();
    private SoftwareTpm? tpm;
    private string directory = string.Empty;
    private KeyFsHost? keyfsHost;
    private KeyFsClient? admin;
    private AuthServerHost? server;
    private IReadOnlyList<SpeaksForRule> rules = [];
    private TimeSpan lifetime = TimeSpan.FromMinutes(10);
    private AuthClient? client;
    private byte[] challenge = [];
    private byte[]? tickets;

    private bool[] concurrentResults = [];

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
            await AddUser(row["user"], row["password"]);
        }
    }

    [Given("an auth server on that keyfs")]
    public void GivenServer() => StartServer();

    [Given("the auth server's speaks-for rules")]
    public async Task GivenRules(Table table)
    {
        rules = table.Rows.Select(row => new SpeaksForRule(row["hostid"], row["uid"])).ToArray();
        await RestartServer();
    }

    [Given(@"^the auth server closes connections (\d+) seconds after they open$")]
    public async Task GivenLifetime(int seconds)
    {
        lifetime = TimeSpan.FromSeconds(seconds);
        await RestartServer();
    }

    [Given(@"^the keyfs has the user ""(.*)"" with password ""(.*)""$")]
    public Task GivenUser(string user, string password) => AddUser(user, password);

    [Given(@"^the keyfs user ""(.*)"" is (unknown|disabled|expired|in purgatory after 10 bad attempts|without an AES key)$")]
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
            case "expired":
                await admin!.WriteTextAsync($"{user}/expire", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100).ToString(CultureInfo.InvariantCulture));
                break;
            case "without an AES key":
                await admin!.WriteAsync($"{user}/aeskey", new byte[Dp9ikConstants.AesKeyLength]);
                break;
            default:
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    await admin!.WriteTextAsync($"{user}/log", "bad");
                }

                break;
        }
    }

    [When(@"^the keyfs user ""(.*)"" is given the password ""(.*)""$")]
    public async Task WhenPasswordChanged(string user, string password)
    {
        AuthKey key = AuthKey.FromPassword(password);
        await admin!.WriteAsync($"{user}/key", key.DesKey);
        await admin.WriteAsync($"{user}/aeskey", key.AesKey);
        passwords[user] = password;
    }

    [When(@"^a client sends a PAK request with authid ""(.*)"", hostid ""(.*)"" and uid ""(.*)""$")]
    public async Task WhenPakRequest(string authid, string hostid, string uid)
    {
        await EnsureClient();
        await client!.SendAsync(AuthClient.Request(AuthMessageType.AuthPak, authid, hostid, uid));
    }

    [Then("the server replies AuthOK")]
    public async Task ThenAuthOk() => Assert.Equal(AuthClient.AuthOK, await client!.ReadByteAsync());

    [Then(@"^the client completes an AuthPAK exchange as ""(.*)"" and then as ""(.*)""$")]
    public async Task ThenTwoExchanges(string first, string second)
    {
        await Exchange(first);
        await Exchange(second);
    }

    [Then(@"^the client completes an AuthPAK exchange as each of ""(.*)""$")]
    public async Task ThenExchanges(string ids)
    {
        foreach (string id in ids.Split(',', StringSplitOptions.TrimEntries))
        {
            await Exchange(id);
        }
    }

    [Then("the server is ready for the next request")]
    public async Task ThenReady()
    {
        await client!.SendAsync(AuthClient.Request(AuthMessageType.AuthPak, "fog", "glenda", "glenda"));
        Assert.Equal(AuthClient.AuthOK, await client.ReadByteAsync());
    }

    [Then(@"^the server's public values for ""(.*)"" are not all zeros and differ between two exchanges$")]
    public async Task ThenPublicValuesLookReal(string id)
    {
        var values = new List<byte[]>();
        for (int round = 0; round < 2; round++)
        {
            using AuthClient probe = await AuthClient.ConnectAsync(server!.LocalEndPoint);
            await probe.SendAsync(AuthClient.Request(AuthMessageType.AuthPak, string.Empty, id, id));
            Assert.Equal(AuthClient.AuthOK, await probe.ReadByteAsync());
            values.Add(await probe.ReadAsync(Dp9ikConstants.PakPublicValueLength));
        }

        Assert.All(values, value => Assert.NotEqual(new byte[Dp9ikConstants.PakPublicValueLength], value));
        Assert.NotEqual(values[0], values[1]);
    }

    [Given(@"^the keyfs has the user ""(.*)"" with a password whose AES key holds a zero byte$")]
    public Task GivenZeroByteUser(string user)
    {
        string password = Enumerable.Range(0, 10_000).Select(index => $"zero-{index}")
            .First(candidate => AuthKey.FromPassword(candidate).AesKey.Contains((byte)0));
        return AddUser(user, password);
    }

    [When("those clients disconnect")]
    public void WhenClientsDisconnect()
    {
        foreach (AuthClient each in others)
        {
            each.Dispose();
        }

        others.Clear();
    }

    [Then("the auth server tracks no connections")]
    public Task ThenNoConnections() => ThenTracks(0);

    [Then(@"^the auth server tracks (\d+) connections$")]
    public async Task ThenTracks(int count)
    {
        // Connections are noticed and forgotten asynchronously.
        for (int attempt = 0; attempt < 100 && server!.ConnectionCount != count; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(count, server!.ConnectionCount);
    }

    [When("the auth server shuts down")]
    public async Task WhenServerShutsDown()
    {
        await server!.DisposeAsync();
        server = null;
    }

    [Given(@"^a client that completed a PAK request with authid ""(.*)"", hostid ""(.*)"" and uid ""(.*)""(?: using that password)?$")]
    [When(@"^a client that completed a PAK request with authid ""(.*)"", hostid ""(.*)"" and uid ""(.*)""(?: using that password)?$")]
    public async Task GivenPak(string authid, string hostid, string uid)
    {
        await WhenPakRequest(authid, hostid, uid);
        await ThenAuthOk();
        if (hostid.Length > 0)
        {
            if (authid.Length > 0)
            {
                await Exchange(authid);
            }

            await Exchange(hostid);
        }
        else
        {
            await Exchange(uid);
        }
    }

    [When(@"^it answers the server's first public value with an encoding greater than \(p-1\)/2$")]
    public async Task WhenInvalidValue()
    {
        await ThenAuthOk();
        await client!.ReadAsync(Dp9ikConstants.PakPublicValueLength);
        byte[] value = (P - 1).ToByteArray(isUnsigned: true, isBigEndian: true);
        await client.SendAsync(value);
    }

    [When(@"^it sends a ticket request with authid ""(.*)"", hostid ""(.*)"", uid ""(.*)"" and a fresh challenge$")]
    [When(@"^a client sends a ticket request with authid ""(.*)"", hostid ""(.*)"", uid ""(.*)"" and a fresh challenge$")]
    public async Task WhenTicketRequest(string authid, string hostid, string uid)
    {
        await EnsureClient();
        tickets = null;
        TicketRequest request = AuthClient.Request(AuthMessageType.AuthTreq, authid, hostid, uid);
        challenge = request.Challenge.ToArray();
        await client!.SendAsync(request);
    }

    [Given(@"^it sent a ticket request with authid ""(.*)"", hostid ""(.*)"", uid ""(.*)"" and a fresh challenge$")]
    public async Task GivenTicketRequest(string authid, string hostid, string uid)
    {
        await WhenTicketRequest(authid, hostid, uid);
        await ReadTickets();
    }

    [When(@"^it sends a ticket request whose uid field holds 28 bytes ""(.*)""$")]
    public async Task WhenLongUid(string uid)
    {
        TicketRequest request = AuthClient.Request(AuthMessageType.AuthTreq, "fog", uid[..^1], uid[..^1]);
        Encoding.UTF8.GetBytes(uid).CopyTo(request.UserId, 0);
        challenge = request.Challenge.ToArray();
        tickets = null;
        await client!.SendAsync(request);
    }

    [Then("the server replies AuthOK, a form 1 AuthTc ticket and a form 1 AuthTs ticket")]
    public async Task ThenTickets()
    {
        await ReadTickets();
        Assert.Equal("form1 Tc"u8.ToArray(), tickets![..8]);
        Assert.Equal("form1 Ts"u8.ToArray(), tickets[Dp9ikConstants.MaxTicketLength..][..8]);
    }

    [Then(@"^the AuthTc ticket (opens|does not open) with the PAK key the client shares as ""(.*)""$")]
    public async Task ThenTcOutcome(string outcome, string id)
    {
        Ticket? opened = await Open(clientTicket: true, id, expectOpen: outcome == "opens");
        if (opened is not null)
        {
            Assert.Equal(AuthMessageType.AuthTc, opened.Type);
        }
    }

    [Then(@"^the AuthTs ticket opens with the PAK key the client shares as ""(.*)""$")]
    public async Task ThenTsOpens(string id) => Assert.Equal(AuthMessageType.AuthTs, (await Open(clientTicket: false, id))!.Type);

    [Then(@"^both tickets carry the challenge, client user ""(.*)"", server user ""(.*)"" and the same 32-byte key$")]
    public async Task ThenTicketContents(string clientUser, string serverUser)
    {
        Ticket tc = (await Open(clientTicket: true, "glenda"))!;
        Ticket ts = (await Open(clientTicket: false, "fog"))!;
        foreach (Ticket ticket in new[] { tc, ts })
        {
            Assert.Equal(challenge, ticket.Challenge);
            Assert.Equal(clientUser, ticket.ClientUserText);
            Assert.Equal(serverUser, ticket.ServerUserText);
            Assert.Equal(TicketEncryptionForm.Form1, ticket.Form);
        }

        Assert.Equal(tc.SessionKey, ts.SessionKey);
        Assert.NotEqual(new byte[Dp9ikConstants.NonceLength], tc.SessionKey);
    }

    [Given(@"^a client that completed two PAK and ticket request rounds for ""(.*)"" on one connection$")]
    public async Task GivenTwoRounds(string user)
    {
        for (int round = 0; round < 2; round++)
        {
            await GivenPak("fog", user, user);
            await GivenTicketRequest("fog", user, user);
            ticketKeys.Add((await Open(clientTicket: true, user))!.SessionKey);
        }
    }

    [Then("the two ticket keys differ")]
    public void ThenKeysDiffer() => Assert.NotEqual(ticketKeys[0], ticketKeys[1]);

    [Then(@"^the AuthTc ticket's client user is ""(.*)""$")]
    public async Task ThenTcClientUser(string user) => Assert.Equal(user, (await Open(clientTicket: true, user))!.ClientUserText);

    [Then(@"^the server replies AuthErr ""(.*)""$")]
    public async Task ThenAuthErr(string message)
    {
        Assert.Equal(AuthClient.AuthErr, await client!.ReadByteAsync());
        Assert.Equal(message, await client.ReadErrorAsync());
    }

    [Then("the server closes the connection")]
    public async Task ThenClosed() => await client!.DrainUntilClosedAsync(TimeSpan.FromSeconds(10));

    [Then("the server closes the connection without replying")]
    public async Task ThenClosedSilently() => Assert.Equal(0, await client!.DrainUntilClosedAsync(TimeSpan.FromSeconds(10)));

    [When(@"^a client sends a ticket request of type (\w+)$")]
    public async Task WhenRequestOfType(string type)
    {
        await EnsureClient();
        byte[] request = AuthClient.Request(AuthMessageType.AuthTreq, "fog", "glenda", "glenda").Marshal();
        request[0] = RequestTypes[type];
        await client!.SendAsync(request);
    }

    [When(@"^a client sends 100 of the 141 bytes of a ticket request and stops sending$")]
    public async Task WhenPartialRequest()
    {
        await EnsureClient();
        await client!.SendAsync(AuthClient.Request(AuthMessageType.AuthTreq, "fog", "glenda", "glenda").Marshal()[..100]);
        client.StopSending();
    }

    [When("a client sends a PAK request and stops sending before its public value")]
    public async Task WhenPakThenStop()
    {
        await WhenPakRequest("fog", "glenda", "glenda");
        await ThenAuthOk();
        await client!.ReadAsync(Dp9ikConstants.PakPublicValueLength);
        client.StopSending();
    }

    [Then("the auth server goes on serving other clients")]
    public async Task ThenServesOthers()
    {
        client!.Dispose();
        client = null;
        await GivenPak("fog", "glenda", "glenda");
        await WhenTicketRequest("fog", "glenda", "glenda");
        await ThenTcOutcome("opens", "glenda");
    }

    [When(@"^a client connects and sends nothing for (\d+) seconds$")]
    public async Task WhenIdle(int seconds)
    {
        await EnsureClient();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }

    [Then("the server has closed the connection")]
    public async Task ThenHasClosed() => Assert.Equal(0, await client!.DrainUntilClosedAsync(TimeSpan.FromSeconds(1)));

    [When(@"^(\d+) clients each complete a PAK and ticket request round for ""(.*)"" at the same time$")]
    public async Task WhenConcurrentRounds(int count, string user)
    {
        var rounds = Enumerable.Range(0, count).Select(async round =>
        {
            AuthClient each = await AuthClient.ConnectAsync(server!.LocalEndPoint);
            lock (others)
            {
                others.Add(each);
            }

            await each.SendAsync(AuthClient.Request(AuthMessageType.AuthPak, "fog", user, user));
            Assert.Equal(AuthClient.AuthOK, await each.ReadByteAsync());
            await each.PakAsync(AuthClient.Key(passwords["fog"], "fog"));
            AuthKey hostKey = await each.PakAsync(AuthClient.Key(passwords[user], user));
            await each.SendAsync(AuthClient.Request(AuthMessageType.AuthTreq, "fog", user, user));
            Assert.Equal(AuthClient.AuthOK, await each.ReadByteAsync());
            byte[] received = await each.ReadTicketsAsync();
            return Ticket.TryUnmarshal(hostKey, received.AsSpan(0, Dp9ikConstants.MaxTicketLength), out _, out _);
        });
        concurrentResults = await Task.WhenAll(rounds);
    }

    [Then("every client receives tickets that open with its own PAK key")]
    public void ThenAllOpen() => Assert.All(concurrentResults, Assert.True);

    [Then("the auth server listens on its configured TCP endpoint")]
    public async Task ThenListens()
    {
        Assert.Equal(IPAddress.Loopback, server!.LocalEndPoint.Address);
        Assert.NotEqual(0, server.LocalEndPoint.Port);
        using AuthClient probe = await AuthClient.ConnectAsync(server.LocalEndPoint);
    }

    [Then("it stops listening when the host shuts down")]
    public async Task ThenStopsListening()
    {
        IPEndPoint endPoint = server!.LocalEndPoint;
        await server.DisposeAsync();
        server = null;
        await Assert.ThrowsAsync<SocketException>(() => AuthClient.ConnectAsync(endPoint));
    }

    [AfterScenario]
    public async Task CleanUpAsync()
    {
        client?.Dispose();
        foreach (AuthClient each in others)
        {
            each.Dispose();
        }

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

    private async Task AddUser(string user, string password)
    {
        AuthKey key = AuthKey.FromPassword(password);
        await admin!.CreateAsync(string.Empty, user, isDirectory: true);
        await admin.WriteAsync($"{user}/key", key.DesKey);
        await admin.WriteAsync($"{user}/aeskey", key.AesKey);
        passwords[user] = password;
    }

    private void StartServer()
        => server = AuthServerHost.Start(
            new AuthServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), SpeaksFor = rules, ConnectionLifetime = lifetime },
            keyfsHost!);

    private async Task RestartServer()
    {
        client?.Dispose();
        client = null;
        if (server is not null)
        {
            await server.DisposeAsync();
        }

        StartServer();
    }

    private async Task EnsureClient() => client ??= await AuthClient.ConnectAsync(server!.LocalEndPoint);

    private async Task Exchange(string id) => clientKeys[id] = await client!.PakAsync(AuthClient.Key(passwords[id], id));

    private async Task ReadTickets()
    {
        if (tickets is not null)
        {
            return;
        }

        Assert.Equal(AuthClient.AuthOK, await client!.ReadByteAsync());
        tickets = await client.ReadTicketsAsync();
    }

    private async Task<Ticket?> Open(bool clientTicket, string id, bool expectOpen = true)
    {
        await ReadTickets();
        byte[] ticket = clientTicket ? tickets![..Dp9ikConstants.MaxTicketLength] : tickets![Dp9ikConstants.MaxTicketLength..];
        bool opened = Ticket.TryUnmarshal(clientKeys[id], ticket, out Ticket? read, out _);
        Assert.Equal(expectOpen, opened);
        return read;
    }
}
