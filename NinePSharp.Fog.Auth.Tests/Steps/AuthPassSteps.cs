using System.Globalization;
using System.Net;
using Dp9ik;
using NinePSharp.Fog.Auth.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests.Steps;

[Binding]
[Scope(Feature = "Users change their password and secret as 9front auth/passwd does")]
public sealed class AuthPassSteps
{
    private static readonly string ZeroBytePassword = Enumerable.Range(0, 10_000).Select(index => $"zero-byte-{index}")
        .First(candidate => AuthKey.FromPassword(candidate).AesKey.Contains((byte)0));

    private readonly Dictionary<string, string> passwords = new(StringComparer.Ordinal);
    private readonly SwitchableFiles files = new();
    private SoftwareTpm? tpm;
    private string directory = string.Empty;
    private KeyFsHost? keyfsHost;
    private KeyFsClient? admin;
    private AuthServerHost? server;
    private AuthClient? client;
    private AuthKey? pakKey;
    private byte[] challenge = [];
    private byte[]? ticketBytes;
    private Ticket? ticket;

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
            Files = files,
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

    [Given(@"^the keyfs user ""(.*)"" is (unknown|disabled|expired|in purgatory after 10 bad attempts)$")]
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
            default:
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    await admin!.WriteTextAsync($"{user}/log", "bad");
                }

                break;
        }
    }

    [Given(@"^the keyfs user ""(.*)"" has the DES key of ""(.*)"" and the AES key of ""(.*)""$")]
    public async Task GivenMixedKeys(string user, string desPassword, string aesPassword)
    {
        await admin!.WriteAsync($"{user}/key", AuthKey.FromPassword(desPassword).DesKey);
        await admin.WriteAsync($"{user}/aeskey", AuthKey.FromPassword(aesPassword).AesKey);
    }

    [Given(@"^the keyfs log of ""(.*)"" reads ""(\d+)""$")]
    public async Task GivenLog(string user, int count)
    {
        for (int attempt = 0; attempt < count; attempt++)
        {
            await admin!.WriteTextAsync($"{user}/log", "bad");
        }

        await ThenLog(user, count.ToString(CultureInfo.InvariantCulture));
    }

    [Given("saving the keyfs database fails")]
    public void GivenSavingFails() => files.Failing = true;

    [Given(@"^""(.*)"" has an AuthTp ticket after a PAK exchange with password ""(.*)""$")]
    public async Task GivenTicket(string user, string password)
    {
        await WhenAskForTicket(user, password);
        Assert.Equal(AuthClient.AuthOK, await client!.ReadByteAsync());
        ticketBytes = await client.ReadAsync(Dp9ikConstants.MaxTicketLength);
        Assert.True(Ticket.TryUnmarshal(pakKey!, ticketBytes, out ticket, out _));
    }

    [When(@"^""(.*)"" asks for an AuthTp ticket after a PAK exchange with password ""(.*)""$")]
    public async Task WhenAskForTicket(string user, string password)
    {
        client = await AuthClient.ConnectAsync(server!.LocalEndPoint);
        await client.SendAsync(AuthClient.Request(AuthMessageType.AuthPak, string.Empty, string.Empty, user));
        Assert.Equal(AuthClient.AuthOK, await client.ReadByteAsync());
        pakKey = await client.PakAsync(AuthClient.Key(password, user));
        TicketRequest request = AuthClient.Request(AuthMessageType.AuthPass, string.Empty, string.Empty, user);
        challenge = request.Challenge.ToArray();
        await client.SendAsync(request);
    }

    [When(@"^a client sends an AuthPass request for ""(.*)"" after a PAK exchange as host ""(.*)"" for ""(.*)""$")]
    public async Task WhenAuthPassAfterHostPak(string user, string host, string authid)
    {
        client = await AuthClient.ConnectAsync(server!.LocalEndPoint);
        await client.SendAsync(AuthClient.Request(AuthMessageType.AuthPak, authid, host, user));
        Assert.Equal(AuthClient.AuthOK, await client.ReadByteAsync());
        await client.PakAsync(AuthClient.Key(passwords[authid], authid));
        await client.PakAsync(AuthClient.Key(passwords[host], host));
        await client.SendAsync(AuthClient.Request(AuthMessageType.AuthPass, string.Empty, string.Empty, user));
    }

    [When(@"^a client sends an AuthPass request for ""(.*)"" without a PAK exchange$")]
    [When(@"^the client sends an AuthPass request for ""(.*)"" without a PAK exchange$")]
    public async Task WhenAuthPassWithoutPak(string user)
    {
        client ??= await AuthClient.ConnectAsync(server!.LocalEndPoint);
        await client.SendAsync(AuthClient.Request(AuthMessageType.AuthPass, string.Empty, string.Empty, user));
    }

    [When(@"^the client asks to change the password from ""(.*)"" to ""(.*)""$")]
    public Task WhenChangePassword(string old, string @new) => SendRequest(old, Unescape(@new), secret: null);

    [Given(@"^the client asked to change the password from ""(.*)"" to ""(.*)"" and got AuthOK$")]
    public async Task GivenChanged(string old, string @new)
    {
        await WhenChangePassword(old, @new);
        await ThenAuthOk();
    }

    [When(@"^the client asks to keep the password ""(.*)"" and set the secret ""(.*)""$")]
    public Task WhenChangeSecret(string old, string secret) => SendRequest(old, string.Empty, secret);

    [When("the client sends a password request sealed with another key")]
    public async Task WhenWrongKey()
    {
        var other = new Ticket(AuthMessageType.AuthTp, TicketEncryptionForm.Form1);
        other.SetSessionKey(AuthKey.CreateRandom().SharedKey);
        var request = new PasswordRequest(AuthMessageType.AuthPass);
        request.SetOldPassword("glenda-password");
        await client!.SendAsync(request.Marshal(other));
    }

    [Then("the server replies AuthOK")]
    public async Task ThenAuthOk() => Assert.Equal(AuthClient.AuthOK, await client!.ReadByteAsync());

    [Then(@"^the server replies AuthErr ""(.*)""$")]
    public async Task ThenAuthErr(string message)
    {
        Assert.Equal(AuthClient.AuthErr, await client!.ReadByteAsync());
        Assert.Equal(message, await client.ReadErrorAsync());
    }

    [Then("the server closes the connection")]
    public async Task ThenClosed() => await client!.DrainUntilClosedAsync(TimeSpan.FromSeconds(10));

    [Then("the server replies AuthOK and a form 1 ticket that does not open with that PAK key")]
    public async Task ThenUnopenable()
    {
        Assert.Equal(AuthClient.AuthOK, await client!.ReadByteAsync());
        byte[] sealedTicket = await client.ReadAsync(Dp9ikConstants.MaxTicketLength);
        Assert.Equal("form1 Tp"u8.ToArray(), sealedTicket[..8]);
        Assert.False(Ticket.TryUnmarshal(pakKey!, sealedTicket, out _, out _));
    }

    [Then(@"^the ticket is a form 1 AuthTp ticket for client user ""(.*)"" and server user ""(.*)"" with the request's challenge$")]
    public void ThenTicket(string clientUser, string serverUser)
    {
        Assert.Equal("form1 Tp"u8.ToArray(), ticketBytes![..8]);
        Assert.Equal(AuthMessageType.AuthTp, ticket!.Type);
        Assert.Equal(TicketEncryptionForm.Form1, ticket.Form);
        Assert.Equal(clientUser, ticket.ClientUserText);
        Assert.Equal(serverUser, ticket.ServerUserText);
        Assert.Equal(challenge, ticket.Challenge);
        Assert.NotEqual(new byte[Dp9ikConstants.NonceLength], ticket.SessionKey);
    }

    [Then(@"^the keyfs holds the DES and AES keys of ""(.*)"" for ""(.*)""$")]
    public async Task ThenKeys(string password, string user)
    {
        AuthKey key = AuthClient.Key(password, user);
        Assert.Equal(key.DesKey, await admin!.ReadAsync($"{user}/key"));
        Assert.Equal(key.AesKey, await admin.ReadAsync($"{user}/aeskey"));
        Assert.Equal(key.PakHash, await admin.ReadAsync($"{user}/pakhash"));
    }

    [When("the client changes the password to one whose AES key holds a zero byte")]
    public Task WhenChangeToZeroByte() => WhenChangePassword("glenda-password", ZeroBytePassword);

    [Then(@"^the keyfs holds the DES and AES keys of that password for ""(.*)""$")]
    public Task ThenKeysOfThatPassword(string user) => ThenKeys(ZeroBytePassword, user);

    [Given(@"^the keyfs user ""(.*)"" has the DES key of ""(.*)"" and the AES key of a password whose AES key holds a zero byte$")]
    public Task GivenZeroByteAes(string user, string desPassword) => GivenMixedKeys(user, desPassword, ZeroBytePassword);

    [Given(@"^""(.*)"" has an AuthTp ticket after a PAK exchange with that password$")]
    public Task GivenTicketWithThatPassword(string user) => GivenTicket(user, ZeroBytePassword);

    [Then(@"^the keyfs secret of ""(.*)"" reads ""(.*)""$")]
    public async Task ThenSecret(string user, string secret) => Assert.Equal(secret, await admin!.ReadTextAsync($"{user}/secret"));

    [Then(@"^the keyfs log of ""(.*)"" reads ""(.*)""$")]
    public async Task ThenLog(string user, string count) => Assert.Equal(count, await admin!.ReadTextAsync($"{user}/log"));

    [Then(@"^""(.*)"" can get tickets with password ""(.*)"" and not with ""(.*)""$")]
    public async Task ThenTicketsWith(string user, string good, string bad)
    {
        Assert.True(await TicketOpens(user, good));
        Assert.False(await TicketOpens(user, bad));
    }

    [AfterScenario]
    public async Task CleanUpAsync()
    {
        client?.Dispose();
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

    private static string Unescape(string text) => text.Replace("\\s", " ", StringComparison.Ordinal);

    private async Task SendRequest(string old, string @new, string? secret)
    {
        var request = new PasswordRequest(AuthMessageType.AuthPass);
        request.SetOldPassword(old);
        request.SetNewPassword(@new);
        request.ChangeSecret = secret is not null;
        request.SetSecret(secret ?? string.Empty);
        await client!.SendAsync(request.Marshal(ticket!));
    }

    private async Task<bool> TicketOpens(string user, string password)
    {
        using AuthClient round = await AuthClient.ConnectAsync(server!.LocalEndPoint);
        await round.SendAsync(AuthClient.Request(AuthMessageType.AuthPak, "fog", user, user));
        Assert.Equal(AuthClient.AuthOK, await round.ReadByteAsync());
        await round.PakAsync(AuthClient.Key(passwords["fog"], "fog"));
        AuthKey hostKey = await round.PakAsync(AuthClient.Key(password, user));
        await round.SendAsync(AuthClient.Request(AuthMessageType.AuthTreq, "fog", user, user));
        Assert.Equal(AuthClient.AuthOK, await round.ReadByteAsync());
        byte[] tickets = await round.ReadTicketsAsync();
        return Ticket.TryUnmarshal(hostKey, tickets.AsSpan(0, Dp9ikConstants.MaxTicketLength), out _, out _);
    }
}
