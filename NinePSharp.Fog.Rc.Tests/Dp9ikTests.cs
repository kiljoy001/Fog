using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NinePSharp.Constants;
using NinePSharp.Fog.Auth;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// factotum's dp9ik and p9any, spoken through its rpc file as a client and a server speak them to
// each other: the tickets made up for the host's own user when there is no auth server, or got from
// Fog's authsrv, the authinfo and capability of an authentication done, a login, and each way a
// conversation is refused.
public sealed class Dp9ikTests : IAsyncLifetime
{
    private const string Server = "proto=dp9ik role=server";
    private const string Client = "proto=dp9ik role=client";
    private readonly FakeHostNetwork host = new();
    private readonly FakeHostConsole console = new();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string> passwords = new() { ["glenda"] = "glendapw", ["bob"] = "bobpassw" };
    private readonly List<SpeaksForRule> rules = [];
    private readonly CancellationTokenSource stopping = new();
    private FogKernel kernel = null!;
    private Process init = null!;
    private TcpListener? listener;
    private int served;

    private string AuthAddr => "tcp!127.0.0.1!17567";

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", clock, host, console);
        init = await kernel.BootAsync();
        for (int i = 0; i < 3; i++)
        {
            await init.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }
    }

    public Task DisposeAsync()
    {
        stopping.Cancel();
        listener?.Stop();
        foreach (Process process in kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheHostsOwnUserIsAuthenticatedWithTicketsMadeUpWhenThereIsNoAuthServer()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        await ExchangeAsync(client, server);

        (string cuid, string suid, string cap, byte[] secret) = await AuthinfoAsync(server);
        Assert.Equal(("glenda", "glenda"), (cuid, suid));
        Assert.StartsWith("glenda@glenda@", cap, StringComparison.Ordinal);
        (string ccuid, string csuid, string ccap, byte[] csecret) = await AuthinfoAsync(client);
        Assert.Equal(("glenda", "glenda"), (ccuid, csuid));
        Assert.Equal(secret, csecret);
        Assert.Equal(256, secret.Length);
        Assert.StartsWith("glenda@glenda@", ccap, StringComparison.Ordinal);

        // What is done says so, with the authinfo still to be had.
        Assert.Equal(("done", "haveai"), await client.TextAsync("read"));
        Assert.Equal(("done", "haveai"), await client.TextAsync("write", "x"u8.ToArray()));
    }

    [Fact]
    public async Task TheAuthServerGivesTheTicketsAndTheServersCapabilityMakesItsProcessTheUser()
    {
        await ServeAsync();
        await StartAsync("/tmp/a", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", $"{Client} user=bob");
        await ExchangeAsync(client, server);
        Assert.Equal(1, served);

        (string cuid, string suid, string cap, byte[] secret) = await AuthinfoAsync(server);
        Assert.Equal(("bob", "bob"), (cuid, suid));
        Assert.Equal(secret, (await AuthinfoAsync(client)).Secret);

        // The capability is spent once, as the host owner who asked for it.
        var became = new TaskCompletionSource<string>();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async c =>
        {
            int use = await c.OpenAsync("/dev/capuse", NinePConstants.OWRITE);
            await c.WriteAsync(use, Encoding.UTF8.GetBytes(cap));
            became.SetResult(c.User);
        });
        Assert.Equal("bob", await became.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AKeyOfAnotherDomainIsNoKeyToSpeakForTheHostsOwnUser()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik role=server user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=other !password=otherpwd");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        byte[] pakreq = await PakreqAsync(client, server);
        Assert.Equal("needkey", (await client.CallAsync("write", pakreq)).Status);
    }

    [Fact]
    public async Task AnyUsersKeyOfTheDomainSpeaksForTheHostsOwnUserNamedInTheConversation()
    {
        // The auth server lets bob speak for glenda; without speaking for her, the conversation
        // would want a key of her own.
        await ServeAsync();
        rules.Add(new SpeaksForRule("bob", "glenda"));
        await StartAsync("/tmp/a");
        await StartAsync("/tmp/b", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik role=server user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/b", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/b", $"{Client} user=glenda");
        byte[] pakreq = await PakreqAsync(client, server);
        Assert.Equal("ok", (await client.CallAsync("write", pakreq)).Status);
    }

    [Fact]
    public async Task OfMoreThanSevenAuthServersGivenTheLastTakesTheSeventhsPlace()
    {
        await ServeAsync();
        string[] refused = [.. Enumerable.Repeat((string[])["-a", "tcp!127.0.0.1!1"], 6).SelectMany(a => a)];

        // The seventh is dialed once the six before it refuse.
        await StartAsync("/tmp/a", [.. refused, "-a", AuthAddr]);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", $"{Client} user=bob");
        await ExchangeAsync(client, server);
        Assert.Equal(1, served);

        // An eighth takes its place, and no auth server is reached.
        await StartAsync("/tmp/b", [.. refused, "-a", AuthAddr, "-a", "tcp!127.0.0.1!1"]);
        await KeyAsync("/tmp/b", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/b", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        server = await ConvAsync("/tmp/b", Server);
        client = await ConvAsync("/tmp/b", $"{Client} user=bob");
        byte[] pakreq = await PakreqAsync(client, server);
        Assert.Equal("error", (await client.CallAsync("write", pakreq)).Status);
        Assert.Equal(1, served);
    }

    [Fact]
    public async Task AWrongPasswordDisablesTheKeyAndAsksForAnotherButOnAServerIsBadAndLeftAlone()
    {
        await ServeAsync();
        await StartAsync("/tmp/a", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=wrongpwd");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", $"{Client} dom=fog user=bob");
        byte[] pakreq = await PakreqAsync(client, server);
        Assert.Equal(("needkey", "proto=dp9ik dom=fog user=bob user? !password?"), await client.TextAsync("write", pakreq));
        Assert.Contains("key proto=dp9ik user=bob dom=fog disabled=by.factotum !password?\n", await CtlAsync("/tmp/a"), StringComparison.Ordinal);

        // A server's factotum, which asks for nothing, says the key is bad and keeps it.
        await StartAsync("/tmp/s", "-S", "-a", AuthAddr);
        await KeyAsync("/tmp/s", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/s", "key proto=dp9ik user=bob dom=fog !password=wrongpwd");
        server = await ConvAsync("/tmp/s", Server);
        client = await ConvAsync("/tmp/s", $"{Client} user=bob");
        pakreq = await PakreqAsync(client, server);
        Assert.Equal(("error", "bad key"), await client.TextAsync("write", pakreq));
        Assert.DoesNotContain("disabled", await CtlAsync("/tmp/s"), StringComparison.Ordinal);

        // A key that has worked before is not disabled when it fails.
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        server = await ConvAsync("/tmp/a", Server);
        client = await ConvAsync("/tmp/a", $"{Client} user=bob");
        await ExchangeAsync(client, server);
        passwords["bob"] = "changedp";
        server = await ConvAsync("/tmp/a", Server);
        client = await ConvAsync("/tmp/a", $"{Client} user=bob");
        pakreq = await PakreqAsync(client, server);
        Assert.Equal("needkey", (await client.TextAsync("write", pakreq)).Status);
        Assert.DoesNotContain("user=bob dom=fog disabled", await CtlAsync("/tmp/a"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostSpeaksForAUserOnlyAsTheAuthServerLetsIt()
    {
        await ServeAsync();
        await StartAsync("/tmp/a", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");

        // A user who is not factotum's owner is spoken for by the host's key, which is not theirs.
        rules.Add(new SpeaksForRule("glenda", "none"));
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client, "none");
        await ExchangeAsync(client, server);
        Assert.Equal(("none", "none"), ((await AuthinfoAsync(server)).Cuid, (await AuthinfoAsync(server)).Suid));

        // Without the auth server's leave the tickets do not open, which is bad, not a key to ask for.
        rules.Clear();
        server = await ConvAsync("/tmp/a", Server);
        client = await ConvAsync("/tmp/a", Client, "none");
        byte[] pakreq = await PakreqAsync(client, server);
        Assert.Equal(("error", "bad key"), await client.TextAsync("write", pakreq));
        Assert.DoesNotContain("disabled", await CtlAsync("/tmp/a"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoginChecksTheUsersPasswordWithTheAuthServerOrForTheHostsOwnUserWithoutOne()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        Assert.Equal("ok", (await login.CallAsync("write", "glenda"u8.ToArray())).Status);
        Assert.Equal("ok", (await login.CallAsync("write", "glendapw\0junk"u8.ToArray())).Status);
        Assert.Equal(("glenda", "glenda"), ((await AuthinfoAsync(login)).Cuid, (await AuthinfoAsync(login)).Suid));

        login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        await login.CallAsync("write", "glenda"u8.ToArray());
        Assert.Equal(("error", "bad key"), await login.TextAsync("write", "glendapx"u8.ToArray()));
        login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        await login.CallAsync("write", "bob"u8.ToArray());
        Assert.Equal("error", (await login.CallAsync("write", "bobpassw"u8.ToArray())).Status);

        await ServeAsync();
        await StartAsync("/tmp/s", "-a", AuthAddr);
        await KeyAsync("/tmp/s", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        login = await ConvAsync("/tmp/s", "proto=dp9ik role=login");
        await login.CallAsync("write", "bob"u8.ToArray());
        Assert.Equal("ok", (await login.CallAsync("write", "bobpassw"u8.ToArray())).Status);
        Assert.Equal(("bob", "bob"), ((await AuthinfoAsync(login)).Cuid, (await AuthinfoAsync(login)).Suid));
        login = await ConvAsync("/tmp/s", "proto=dp9ik role=login");
        await login.CallAsync("write", "bob"u8.ToArray());
        Assert.Equal(("error", "bad key"), await login.TextAsync("write", "bobpassx"u8.ToArray()));

        // A password is what comes before a NUL in it, which may be nothing.
        await StartAsync("/tmp/e");
        await KeyAsync("/tmp/e", "key proto=dp9ik user=glenda dom=fog !password=''");
        login = await ConvAsync("/tmp/e", "proto=dp9ik role=login");
        await login.CallAsync("write", "glenda"u8.ToArray());
        Assert.Equal("ok", (await login.CallAsync("write", "\0junk"u8.ToArray())).Status);

        // A user or password too long for its field.
        login = await ConvAsync("/tmp/s", "proto=dp9ik role=login");
        Assert.Equal(("error", "user id too long"), await login.TextAsync("write", Encoding.UTF8.GetBytes(new string('u', 28))));
        Assert.Equal("ok", (await login.CallAsync("write", Encoding.UTF8.GetBytes(new string('u', 27)))).Status);
        Assert.Equal(("error", "password too long"), await login.TextAsync("write", Encoding.UTF8.GetBytes(new string('p', 28))));
    }

    [Fact]
    public async Task P9anyOffersEachKeysProtocolAndDomainAndRunsTheOneChosen()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=other !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", "proto=p9any role=server"), client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        (string status, byte[] offer) = await server.CallAsync("read");
        Assert.Equal(("ok", "dp9ik@fog dp9ik@other\0"), (status, Encoding.UTF8.GetString(offer)));
        Assert.Equal("ok", (await client.CallAsync("write", offer)).Status);
        (status, byte[] chosen) = await client.CallAsync("read");
        Assert.Equal(("ok", "dp9ik fog\0"), (status, Encoding.UTF8.GetString(chosen)));
        Assert.Equal("ok", (await server.CallAsync("write", chosen)).Status);
        await ExchangeAsync(client, server);
        Assert.Equal(("glenda", "glenda"), ((await AuthinfoAsync(client)).Cuid, (await AuthinfoAsync(server)).Suid));
        Assert.Contains("proto=dp9ik", (await server.TextAsync("attr")).Text, StringComparison.Ordinal);
        Assert.Contains("dom=fog", (await client.TextAsync("attr")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task P9anysClientTakesVersion2AndItsOkAndRefusesAnyOtherVersion()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        Assert.Equal("ok", (await client.CallAsync("write", "v.2 nope@x dp9ik@fog\0"u8.ToArray())).Status);
        Assert.Equal("dp9ik fog\0", Encoding.UTF8.GetString((await client.CallAsync("read")).Data));
        Assert.Equal(("toosmall", "3"), await client.TextAsync("write", "O"u8.ToArray()));
        Assert.Equal(("error", "server gave up"), await client.TextAsync("write", "NO\0"u8.ToArray()));
        Assert.Equal("ok", (await client.CallAsync("write", "OK\0"u8.ToArray())).Status);
        Assert.Equal(8, (await client.CallAsync("read")).Data.Length);

        client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        Assert.Equal(("error", "unknown version of p9any"), await client.TextAsync("write", "v.3 dp9ik@fog\0"u8.ToArray()));
        client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        Assert.Equal(("toosmall", "2048"), await client.TextAsync("write", "dp9ik@fog"u8.ToArray()));
        Assert.Equal(("toosmall", "2048"), await client.TextAsync("write", "x"u8.ToArray()[..0]));
    }

    [Fact]
    public async Task P9anyRefusesWhatItCannotNegotiate()
    {
        await StartAsync("/tmp/a");
        Conv server = await ConvAsync("/tmp/a", "proto=p9any role=server");
        Assert.Equal(("error", Factotum.Enegotiation), await server.TextAsync("read"));

        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        server = await ConvAsync("/tmp/a", "proto=p9any role=server");
        Assert.Equal("ok", (await server.CallAsync("read")).Status);
        Assert.Equal(("toosmall", "10"), await server.TextAsync("write", "dp9ik fog"u8.ToArray()));
        Assert.Equal(("error", Factotum.Ebadarg), await server.TextAsync("write", "dp9ik\0"u8.ToArray()));
        Assert.Equal(("error", Factotum.Enegotiation), await server.TextAsync("write", "pass fog\0"u8.ToArray()));
        Assert.Equal(("error", Factotum.Enegotiation), await server.TextAsync("write", "dp9ik other\0"u8.ToArray()));
        Assert.Equal("ok", (await server.CallAsync("write", "dp9ik fog\0"u8.ToArray())).Status);

        // A client with no key for any offer asks for one for each in turn, or for none it knows fails.
        Conv client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        Assert.Equal(("needkey", "!password? dom=x proto=dp9ik user?"), await client.TextAsync("write", "nope dp9ik@x dp9ik@y\0"u8.ToArray()));
        Assert.Equal(("needkey", "!password? dom=y proto=dp9ik user?"), await client.TextAsync("write", "nope dp9ik@x dp9ik@y\0"u8.ToArray()));
        Assert.Equal("error", (await client.CallAsync("write", "nope dp9ik@x dp9ik@y\0"u8.ToArray())).Status);
        client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        Assert.Equal("error", (await client.CallAsync("write", "nope@x\0"u8.ToArray())).Status);
        client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        Assert.Equal(("error", Factotum.Enegotiation), await client.TextAsync("write", "\0"u8.ToArray()));

        // Neither role, nor a read or write out of turn.
        Assert.Equal(("error", "role not specified"), await (await ConvAsync("/tmp/a", "proto=p9any", start: false)).TextAsync("start", "proto=p9any"u8.ToArray()));
        client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        Assert.Equal(("phase", "protocol phase error: read in state CNeedProtos"), await client.TextAsync("read"));
        server = await ConvAsync("/tmp/a", "proto=p9any role=server");
        Assert.Equal(("phase", "protocol phase error: write in state SHaveProtos"), await server.TextAsync("write", "x\0"u8.ToArray()));
        await server.CallAsync("read");
        Assert.Equal(("phase", "protocol phase error: read in state SNeedProto"), await server.TextAsync("read"));
        client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        await client.CallAsync("write", "v.2 dp9ik@fog\0"u8.ToArray());
        Assert.Equal(("phase", "protocol phase error: write in state CHaveProto"), await client.TextAsync("write", "x"u8.ToArray()));
        await client.CallAsync("read");
        Assert.Equal(("phase", "protocol phase error: read in state CNeedOK"), await client.TextAsync("read"));
    }

    [Fact]
    public async Task P9anyConfirmsAKeyThatAsksToBeAndPassesOnWhatItsProtocolSays()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog confirm=yes !password=glendapw");
        int confirm = await init.OpenAsync("/tmp/a/factotum/confirm", NinePConstants.ORDWR);
        Conv client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        await init.WriteAsync(client.Fd, "write dp9ik@fog\0"u8.ToArray());
        Task<ReadOnlyMemory<byte>> waiting = init.ReadAsync(client.Fd, 8192).AsTask();
        string asked = Encoding.UTF8.GetString((await init.ReadAsync(confirm, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);
        await init.WriteAsync(confirm, Encoding.UTF8.GetBytes($"{asked.Split(' ')[1]} answer=yes"));
        Assert.Equal("ok", Encoding.UTF8.GetString((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Span));

        Conv server = await ConvAsync("/tmp/a", "proto=p9any role=server");
        await server.CallAsync("read");
        await init.WriteAsync(server.Fd, "write dp9ik fog\0"u8.ToArray());
        waiting = init.ReadAsync(server.Fd, 8192).AsTask();
        asked = Encoding.UTF8.GetString((await init.ReadAsync(confirm, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);
        await init.WriteAsync(confirm, Encoding.UTF8.GetBytes($"{asked.Split(' ')[1]} answer=yes"));
        Assert.Equal("ok", Encoding.UTF8.GetString((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Span));

        // The protocol's own toosmall, needkey and error come through as they are.
        Assert.Equal("dp9ik fog\0", (await client.TextAsync("read")).Text);
        (string status, byte[] chal) = await client.CallAsync("read");
        Assert.Equal("ok", (await server.CallAsync("write", chal)).Status);
        Assert.Equal(("toosmall", "197"), await server.TextAsync("read", count: 100));
        Assert.Equal(("toosmall", "197"), await client.TextAsync("write", "x"u8.ToArray()));
    }

    [Fact]
    public async Task Dp9ikRefusesWhatIsShortOrComesOutOfTurnOrDoesNotOpen()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        Assert.Equal("phase", (await client.CallAsync("write", "x"u8.ToArray())).Status);
        Assert.Equal("phase", (await server.CallAsync("read")).Status);
        Assert.Equal(("toosmall", "8"), await server.TextAsync("write", "short"u8.ToArray()));
        byte[] chal = (await client.CallAsync("read")).Data;
        Assert.Equal("ok", (await server.CallAsync("write", chal)).Status);
        Assert.Equal(("toosmall", "197"), await server.TextAsync("read", count: 100));
        byte[] pakreq = (await server.CallAsync("read")).Data;
        Assert.Equal(("toosmall", "197"), await client.TextAsync("write", pakreq[..196]));
        byte[] treq = [.. pakreq];
        treq[0] = Authsrv.AuthTreq;
        Assert.Equal(("error", Factotum.Easproto), await client.TextAsync("write", treq));
        Assert.Equal("ok", (await client.CallAsync("write", pakreq)).Status);
        byte[] y = (await client.CallAsync("read")).Data;
        Assert.Equal(("toosmall", "56"), await server.TextAsync("write", y[..55]));
        Assert.Equal(("error", Factotum.Easproto), await server.TextAsync("write", Enumerable.Repeat((byte)0xff, 56).ToArray()));

        // The ticket and authenticator, once the AuthPAK values are through.
        server = await ConvAsync("/tmp/a", Server);
        client = await ConvAsync("/tmp/a", Client);
        await ToTicketAsync(client, server);
        Assert.Equal(("toosmall", "192"), await client.TextAsync("read", count: 100));
        byte[] tickets = (await client.CallAsync("read")).Data;
        Assert.Equal(("toosmall", "192"), await server.TextAsync("write", tickets[..191]));
        byte[] broken = [.. tickets];
        broken[30] ^= 1;
        Assert.Equal(("error", Factotum.Easproto), await server.TextAsync("write", broken));
        broken = [.. tickets];
        broken[150] ^= 1;
        Assert.Equal(("error", Factotum.Easproto), await server.TextAsync("write", broken));
        Assert.Equal("ok", (await server.CallAsync("write", tickets)).Status);
        Assert.Equal(("toosmall", "68"), await server.TextAsync("read", count: 64));
        byte[] auth = (await server.CallAsync("read")).Data;
        Assert.Equal(("toosmall", "68"), await client.TextAsync("write", auth[..67]));
        broken = [.. auth];
        broken[20] ^= 1;
        Assert.Equal(("error", Factotum.Easproto), await client.TextAsync("write", broken));
        Assert.Equal("ok", (await client.CallAsync("write", auth)).Status);
        Assert.Equal(("error", "authentication unfinished"), await (await ConvAsync("/tmp/a", Client)).TextAsync("authinfo"));
    }

    [Fact]
    public async Task Dp9ikRefusesTicketsAndAuthenticatorsOfAnotherConversationOrForm()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");

        // The tickets of one conversation do not open in another, and DES ones are refused.
        Conv server1 = await ConvAsync("/tmp/a", Server), client1 = await ConvAsync("/tmp/a", Client);
        Conv server2 = await ConvAsync("/tmp/a", Server), client2 = await ConvAsync("/tmp/a", Client);
        await ToTicketAsync(client1, server1);
        await ToTicketAsync(client2, server2);
        byte[] tickets1 = (await client1.CallAsync("read")).Data, tickets2 = (await client2.CallAsync("read")).Data;
        Assert.Equal(("error", Factotum.Easproto), await server1.TextAsync("write", [.. tickets1[..124], .. tickets2[124..]]));
        Assert.Equal(("error", Factotum.Easproto), await server2.TextAsync("write", Form0(tickets2)));

        // The server's authenticator of another conversation.
        server1 = await ConvAsync("/tmp/a", Server);
        client1 = await ConvAsync("/tmp/a", Client);
        server2 = await ConvAsync("/tmp/a", Server);
        client2 = await ConvAsync("/tmp/a", Client);
        await ToTicketAsync(client1, server1);
        await ToTicketAsync(client2, server2);
        Assert.Equal("ok", (await server1.CallAsync("write", (await client1.CallAsync("read")).Data)).Status);
        Assert.Equal("ok", (await server2.CallAsync("write", (await client2.CallAsync("read")).Data)).Status);
        byte[] auth2 = (await server2.CallAsync("read")).Data;
        Assert.Equal(("error", Factotum.Easproto), await client1.TextAsync("write", auth2));
    }

    [Fact]
    public async Task Dp9ikNeedsARoleAKeyAndATicketRequestItCanFill()
    {
        await StartAsync("/tmp/a");
        Assert.Equal(("error", "role not specified"), await (await ConvAsync("/tmp/a", "proto=dp9ik", start: false)).TextAsync("start", "proto=dp9ik"u8.ToArray()));
        Assert.Equal(("needkey", "dom? proto=dp9ik user?"), await (await ConvAsync("/tmp/a", "x", start: false)).TextAsync("start", Encoding.UTF8.GetBytes(Server)));

        // A key with no domain gives an empty one, which no ticket request may have.
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom='' !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        byte[] pakreq = await PakreqAsync(client, server);
        Assert.Equal(("error", "bad ticket request"), await client.TextAsync("write", pakreq));

        // Without an auth server, made-up tickets are only for the host's own user.
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        server = await ConvAsync("/tmp/a", $"{Server} dom=fog");
        client = await ConvAsync("/tmp/a", $"{Client} user=bob");
        pakreq = await PakreqAsync(client, server);
        Assert.Equal("error", (await client.CallAsync("write", pakreq)).Status);

        // A client with no key asks for one.
        client = await ConvAsync("/tmp/a", $"{Client} user=carol");
        await client.CallAsync("read");
        Assert.Equal(("needkey", "!password? dom=fog proto=dp9ik user? user=carol"), await client.TextAsync("write", pakreq));
    }

    [Fact]
    public async Task AKeyNeedsAUserAndAPasswordOrAnAesKeyInHex()
    {
        await StartAsync("/tmp/a");
        Assert.Equal("no user attribute", await KeyErrorAsync("/tmp/a", "key proto=dp9ik dom=fog !password=x"));
        Assert.Equal("no key data", await KeyErrorAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog"));
        Assert.Equal("malformed key data", await KeyErrorAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !hex=0011zz"));

        // Hex digits of either case among other characters, as many as the key holds.
        byte[] aes = Dp9ik.AuthKey.FromPassword("glendapw").AesKey;
        string hex = string.Join(':', Convert.ToHexString(aes).Chunk(2).Select((c, i) => i % 2 == 0 ? new string(c) : new string(c).ToLowerInvariant()));
        await KeyAsync("/tmp/a", $"key proto=dp9ik user=glenda dom=fog !hex={hex}ff");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        await ExchangeAsync(client, server);

        // A key replaced is let go of, and a conversation closed with it.
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=other");
        Assert.Equal(1, (await CtlAsync("/tmp/a")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        await init.CloseAsync(server.Fd);
    }

    [Fact]
    public async Task EachPhaseIsNamedInTheErrorOfAReadOrWriteOutOfTurn()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        Assert.Equal(("phase", "protocol phase error: write in state CHaveChal"), await client.TextAsync("write", "x"u8.ToArray()));
        Assert.Equal(("phase", "protocol phase error: read in state SNeedChal"), await server.TextAsync("read"));
        byte[] chal = (await client.CallAsync("read")).Data;
        Assert.Equal(("phase", "protocol phase error: read in state CNeedPAKreq"), await client.TextAsync("read"));
        await server.CallAsync("write", chal);
        Assert.Equal(("phase", "protocol phase error: write in state SHavePAKreq"), await server.TextAsync("write", "x"u8.ToArray()));
        byte[] pakreq = (await server.CallAsync("read", count: 200)).Data;
        Assert.Equal(("phase", "protocol phase error: read in state SNeedPAKy"), await server.TextAsync("read"));
        await client.CallAsync("write", pakreq);
        Assert.Equal(("phase", "protocol phase error: write in state CHavePAKy"), await client.TextAsync("write", "x"u8.ToArray()));
        await server.CallAsync("write", (await client.CallAsync("read")).Data);
        Assert.Equal(("phase", "protocol phase error: read in state SNeedTicket"), await server.TextAsync("read"));
        Assert.Equal(("phase", "protocol phase error: write in state CHaveTicket"), await client.TextAsync("write", "x"u8.ToArray()));
        await server.CallAsync("write", (await client.CallAsync("read", count: 195)).Data);
        Assert.Equal(("phase", "protocol phase error: read in state CNeedAuth"), await client.TextAsync("read"));
        Assert.Equal(("phase", "protocol phase error: write in state SHaveAuth"), await server.TextAsync("write", "x"u8.ToArray()));
        await client.CallAsync("write", (await server.CallAsync("read", count: 71)).Data);

        Conv login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        Assert.Equal(("phase", "protocol phase error: read in state SNeedUser"), await login.TextAsync("read"));
        await login.CallAsync("write", "glenda"u8.ToArray());
        Assert.Equal(("phase", "protocol phase error: read in state SNeedPass"), await login.TextAsync("read"));
    }

    [Fact]
    public async Task TheSecretIsTheNoncesAndTicketKeyOfTheConversationAndEachIsNew()
    {
        int calls = 0;
        Authsrv.Random.Value = b => b.Fill((byte)Interlocked.Increment(ref calls));
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        byte[] chal = (await client.CallAsync("read")).Data;
        Assert.Equal(Enumerable.Repeat((byte)2, 8), chal);
        await server.CallAsync("write", chal);
        byte[] pakreq = (await server.CallAsync("read")).Data;
        Assert.Equal(Enumerable.Repeat((byte)1, 8), pakreq[77..85]);
        await client.CallAsync("write", pakreq);
        await server.CallAsync("write", (await client.CallAsync("read")).Data);
        await server.CallAsync("write", (await client.CallAsync("read")).Data);
        await client.CallAsync("write", (await server.CallAsync("read")).Data);
        byte[] secret = Dp9ik.SessionSecret.Derive(Enumerable.Repeat((byte)4, 32).ToArray(), Enumerable.Repeat((byte)5, 32).ToArray(), Enumerable.Repeat((byte)3, 32).ToArray());
        Assert.Equal(secret, (await AuthinfoAsync(client)).Secret);
        Assert.Equal(secret, (await AuthinfoAsync(server)).Secret);

        // A login's nonces are its own.
        calls = 0;
        Conv login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        await login.CallAsync("write", "glenda"u8.ToArray());
        Assert.Equal("ok", (await login.CallAsync("write", "glendapw"u8.ToArray())).Status);
        byte[] both = Enumerable.Repeat((byte)3, 32).ToArray();
        Assert.Equal(Dp9ik.SessionSecret.Derive(both, both, Enumerable.Repeat((byte)2, 32).ToArray()), (await AuthinfoAsync(login)).Secret);
    }

    [Fact]
    public async Task WithoutATestsSourceEachChallengeAndCapabilityIsRandom()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        byte[] one = (await (await ConvAsync("/tmp/a", Client)).CallAsync("read")).Data;
        byte[] two = (await (await ConvAsync("/tmp/a", Client)).CallAsync("read")).Data;
        Assert.NotEqual(one, two);
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        await ExchangeAsync(client, server);
        Assert.NotEqual((await AuthinfoAsync(server)).Cap, (await AuthinfoAsync(server)).Cap);
    }

    [Fact]
    public async Task AServerConversationOfAnotherUserUsesTheHostsKey()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server, "none"), client = await ConvAsync("/tmp/a", Client);
        await ExchangeAsync(client, server);
        Assert.StartsWith("none@glenda@", (await AuthinfoAsync(server)).Cap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHostSpeaksWithItsKeyForTheRequestsDomainAndAsksForItsConfirmation()
    {
        await ServeAsync();
        await StartAsync("/tmp/a", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=other !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        rules.Add(new SpeaksForRule("bob", "none"));
        Conv server = await ConvAsync("/tmp/a", $"{Server} dom=fog"), client = await ConvAsync("/tmp/a", Client, "none");
        await ExchangeAsync(client, server);
        Assert.Equal("none", (await AuthinfoAsync(client)).Cuid);

        // A key that asks to be confirmed is asked about when it would speak for another; the server's
        // conversation, which has its key, is started first.
        server = await ConvAsync("/tmp/a", $"{Server} dom=fog");
        client = await ConvAsync("/tmp/a", Client, "none");
        await KeyAsync("/tmp/a", "delkey user=bob");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog confirm=yes !password=bobpassw");
        int confirm = await init.OpenAsync("/tmp/a/factotum/confirm", NinePConstants.ORDWR);
        byte[] pakreq = await PakreqAsync(client, server);
        await client.Process.WriteAsync(client.Fd, (byte[])[.. "write "u8, .. pakreq]);
        Task<ReadOnlyMemory<byte>> waiting = client.Process.ReadAsync(client.Fd, 8192).AsTask();
        string asked = Encoding.UTF8.GetString((await init.ReadAsync(confirm, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);
        Assert.Contains("user=bob", asked, StringComparison.Ordinal);
        await init.WriteAsync(confirm, Encoding.UTF8.GetBytes($"{asked.Split(' ')[1]} answer=yes"));
        Assert.Equal("ok", Encoding.UTF8.GetString((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Span));
    }

    [Fact]
    public async Task ANeedkeyAfterABadTicketAsksWithTheConversationsOwnAttributes()
    {
        await ServeAsync();
        await StartAsync("/tmp/a", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=wrongpwd");
        await StartAsync("/tmp/s", "-a", AuthAddr);
        await KeyAsync("/tmp/s", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/s", Server);
        Conv client = await ConvAsync("/tmp/a", "proto=dp9ik role=client user? dom=fog");
        byte[] pakreq = await PakreqAsync(client, server);
        Assert.Equal(("needkey", "proto=dp9ik dom=fog user? !password?"), await client.TextAsync("write", pakreq));
    }

    [Fact]
    public async Task ReadsJustLongEnoughAreAnswered()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        await server.CallAsync("write", (await client.CallAsync("read")).Data);
        (string status, byte[] pakreq) = await server.CallAsync("read", count: 200);
        Assert.Equal(("ok", 197), (status, pakreq.Length));
        await client.CallAsync("write", pakreq);
        await server.CallAsync("write", (await client.CallAsync("read")).Data);
        (status, byte[] tickets) = await client.CallAsync("read", count: 195);
        Assert.Equal(("ok", 192), (status, tickets.Length));
        await server.CallAsync("write", tickets);
        (status, byte[] auth) = await server.CallAsync("read", count: 71);
        Assert.Equal(("ok", 68), (status, auth.Length));
    }

    [Fact]
    public async Task TheServerChecksEachPartOfTheTicketAndAuthenticatorItIsGiven()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        (string, string)[] refused =
        [
            await TicketToServerAsync(form: 0),
            await TicketToServerAsync(num: Authsrv.AuthTc),
            await TicketToServerAsync(chal: true),
            await TicketToServerAsync(authNum: Authsrv.AuthAs),
            await TicketToServerAsync(authChal: true),
        ];
        Assert.All(refused, r => Assert.Equal(("error", Factotum.Easproto), r));
        Assert.Equal(("ok", string.Empty), await TicketToServerAsync());
    }

    [Fact]
    public async Task TheClientChecksTheServersAuthenticatorAndMakesTheSecretOfBothNonces()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Assert.Equal(("error", Factotum.Easproto), (await AuthenticatorToClientAsync(Authsrv.AuthAc, false)).Reply);
        Assert.Equal(("error", Factotum.Easproto), (await AuthenticatorToClientAsync(Authsrv.AuthAs, true)).Reply);
        ((string, string) reply, Conv client, byte[] expected) = await AuthenticatorToClientAsync(Authsrv.AuthAs, false);
        Assert.Equal(("ok", string.Empty), reply);
        Assert.Equal(expected, (await AuthinfoAsync(client)).Secret);
    }

    [Fact]
    public async Task TheAuthServersTicketsAreCheckedEachPartAlone()
    {
        await StartAsync("/tmp/a", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=bobpassw");

        // A client refuses DES tickets.
        await ScriptAsync(new Script { Form = 0 });
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", $"{Client} user=bob");
        Assert.Equal(("error", Factotum.Easproto), await client.TextAsync("write", await PakreqAsync(client, server)));

        // A login refuses DES tickets, a bad AuthPAK value, and tickets not for its challenge or not of
        // one key; an auth server's refusal is its error, which a bad AuthPAK value of its own leaves.
        foreach ((Script script, string error) in new (Script, string)[]
        {
            (new Script { Form = 0 }, Factotum.Easproto),
            (new Script { BadY = true }, Factotum.Easproto),
            (new Script { TcChal = true }, Factotum.Ebadkey),
            (new Script { TcNum = Authsrv.AuthTs }, Factotum.Ebadkey),
            (new Script { TsNum = Authsrv.AuthTc }, Factotum.Ebadkey),
            (new Script { TsChal = true }, Factotum.Ebadkey),
            (new Script { TsKey = true }, Factotum.Ebadkey),
            (new Script { TsForm = 0 }, Factotum.Easproto),
            (new Script { Form = 0, TsForm = 1 }, Factotum.Easproto),
            (new Script { Refuse = true }, "remote: refused"),
            (new Script { BadHostY = true }, "remote: refused"),
        })
        {
            await ScriptAsync(script);
            Conv login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
            await login.CallAsync("write", "bob"u8.ToArray());
            Assert.Equal(("error", error), await login.TextAsync("write", "bobpassw"u8.ToArray()));
        }

        await ScriptAsync(new Script());
        Conv good = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        await good.CallAsync("write", "bob"u8.ToArray());
        Assert.Equal("ok", (await good.CallAsync("write", "bobpassw"u8.ToArray())).Status);
    }

    [Fact]
    public async Task AnAuthServerThatSaysNothingIsGivenUpOnAfterThirtySecondsAndItsCallClosed()
    {
        TaskCompletionSource<Socket> accepted = new();
        listener = new TcpListener(IPAddress.Loopback, host.HostPort(17567));
        listener.Start();
        _ = Task.Run(async () => accepted.SetResult(await listener.AcceptSocketAsync(stopping.Token)));
        await StartAsync("/tmp/a", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", Client);
        byte[] pakreq = await PakreqAsync(client, server);
        Task<(string Status, byte[] Data)> written = client.CallAsync("write", pakreq);
        using Socket call = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while (!written.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        // The tickets made up instead, as for the host's own user, and the call closed.
        Assert.Equal("ok", (await written).Status);
        byte[] rest = new byte[1000];
        int got;
        while ((got = await call.ReceiveAsync(rest).WaitAsync(TimeSpan.FromSeconds(5))) > 0)
        {
        }

        Assert.Equal(0, got);

        // Another user's login, which only the auth server could check, fails as the read was interrupted.
        accepted = new TaskCompletionSource<Socket>();
        _ = Task.Run(async () => accepted.SetResult(await listener.AcceptSocketAsync(stopping.Token)));
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        Conv login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        await login.CallAsync("write", "bob"u8.ToArray());
        Task<(string Status, string Text)> refused = login.TextAsync("write", "bobpassw"u8.ToArray());
        using Socket again = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while (!refused.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        Assert.Equal(("error", "interrupted"), await refused);

        // The alarm is gone with it: the server goes on answering, however long after.
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100);
        await server.CallAsync("write", (await client.CallAsync("read")).Data);
        Assert.Equal("ok", (await server.CallAsync("write", (await client.CallAsync("read")).Data)).Status);
    }

    [Fact]
    public async Task WithoutAnAuthServerTheHostsOwnLoginFailsForAnotherUserWithWhyItCouldNotDial()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bob dom=fog !password=bobpassw");
        Conv login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        await login.CallAsync("write", "bob"u8.ToArray());
        (string status, string why) = await login.TextAsync("write", "bobpassw"u8.ToArray());
        Assert.Equal("error", status);
        Assert.NotEqual(Factotum.Easproto, why);
        Conv server = await ConvAsync("/tmp/a", Server), client = await ConvAsync("/tmp/a", $"{Client} user=bob");
        (status, why) = await client.TextAsync("write", await PakreqAsync(client, server));
        Assert.Equal("error", status);
        Assert.NotEqual(Factotum.Easproto, why);
    }

    [Fact]
    public async Task P9anysOfferAndChoiceAreAnsweredWhenTheyJustFit()
    {
        await StartAsync("/tmp/a");

        // dp9ik@ and a NUL are 7 of the 61 bytes an rpc read of 64 has room for.
        string dom = new('d', 55);
        await KeyAsync("/tmp/a", $"key proto=dp9ik user=glenda dom={dom} !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", "proto=p9any role=server");
        Assert.Equal(("toosmall", "62"), await server.TextAsync("read", count: 64));
        await KeyAsync("/tmp/a", "delkey");
        await KeyAsync("/tmp/a", $"key proto=dp9ik user=glenda dom={dom[1..]} !password=glendapw");
        server = await ConvAsync("/tmp/a", "proto=p9any role=server");
        Assert.Equal("ok", (await server.CallAsync("read", count: 64)).Status);

        Conv client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        Assert.Equal("ok", (await client.CallAsync("write", Encoding.UTF8.GetBytes($"dp9ik@{dom[1..]}\0"))).Status);
        Assert.Equal(("ok", $"dp9ik {dom[1..]}\0"), await client.TextAsync("read", count: 64));
        await KeyAsync("/tmp/a", $"key proto=dp9ik user=glenda dom={dom}x !password=glendapw");
        client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        await client.CallAsync("write", Encoding.UTF8.GetBytes($"dp9ik@{dom}x\0"));
        Assert.Equal(("toosmall", "63"), await client.TextAsync("read", count: 64));
    }

    [Fact]
    public async Task P9anyLogsWhatItsProtocolDoesWhenDebugging()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        await KeyAsync("/tmp/a", "debug");
        int log = await init.OpenAsync("/tmp/a/factotum/log", NinePConstants.OREAD);
        Conv client = await ConvAsync("/tmp/a", "proto=p9any role=client");
        await client.CallAsync("write", "dp9ik@fog\0"u8.ToArray());
        await client.CallAsync("read");
        await client.CallAsync("read");
        await client.CallAsync("write", "x"u8.ToArray());
        var lines = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            lines.Add(Encoding.UTF8.GetString((await init.ReadAsync(log, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span));
        }

        Assert.Contains("1: start proto=p9any role=client yields phase CNeedProtos: ok\n", lines);
        Assert.Contains("1: start proto=dp9ik role=client dom=fog yields phase CHaveChal: ok\n", lines);
        Assert.Contains("1: read 8 in phase CHaveChal yields phase CNeedPAKreq: ok\n", lines);
        Assert.Contains("1: write 1 in phase CNeedPAKreq yields phase CNeedPAKreq: toosmall 197\n", lines);
    }

    [Fact]
    public async Task P9anysClientSpeaksForItsOwnerAsItsOwnerOrForAnotherWithTheHostsKey()
    {
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=bootes dom=fog !password=bootespw");

        // As the owner it may use any key; as another named user, only that user's.
        Assert.Equal("ok", (await (await ConvAsync("/tmp/a", "proto=p9any role=client user=glenda")).CallAsync("write", "dp9ik@fog\0"u8.ToArray())).Status);
        Assert.Equal("ok", (await (await ConvAsync("/tmp/a", "proto=p9any role=client")).CallAsync("write", "dp9ik@fog\0"u8.ToArray())).Status);
        Assert.Equal("needkey", (await (await ConvAsync("/tmp/a", "proto=p9any role=client user=bob")).CallAsync("write", "dp9ik@fog\0"u8.ToArray())).Status);
        Assert.Equal("ok", (await (await ConvAsync("/tmp/a", "proto=p9any role=client user=bootes")).CallAsync("write", "dp9ik@fog\0"u8.ToArray())).Status);
        Assert.Equal("ok", (await (await ConvAsync("/tmp/a", "proto=p9any role=client user=bootes")).CallAsync("write", "dp9ik@x dp9ik@fog\0"u8.ToArray())).Status);
        Assert.Equal("ok", (await (await ConvAsync("/tmp/a", "proto=p9any role=client", "none")).CallAsync("write", "dp9ik@fog\0"u8.ToArray())).Status);

        // An offer without a domain is asked for as one of none, and an offer of only a version fails as none at all.
        Conv client = await ConvAsync("/tmp/a", "proto=p9any role=client user=bob");
        Assert.Equal(("needkey", "!password? dom='' proto=dp9ik user=bob user?"), await client.TextAsync("write", "dp9ik\0"u8.ToArray()));
        Assert.Equal(("error", Factotum.Enegotiation), await (await ConvAsync("/tmp/a", "proto=p9any role=client")).TextAsync("write", "v.2\0"u8.ToArray()));
        client = await ConvAsync("/tmp/a", "proto=p9any role=client user=bob");
        await client.CallAsync("write", "dp9ik@x\0"u8.ToArray());
        Assert.NotEqual(("error", Factotum.Enegotiation), await client.TextAsync("write", "dp9ik@x\0"u8.ToArray()));
    }

    [Fact]
    public async Task FactotumNotTheHostOwnerMakesNoCapability()
    {
        int user = await init.OpenAsync("/dev/user", NinePConstants.OWRITE);
        await init.WriteAsync(user, "none"u8.ToArray());
        await StartAsync("/tmp/a");
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv server = await ConvAsync("/tmp/a", Server, "none"), client = await ConvAsync("/tmp/a", Client, "none");
        await ExchangeAsync(client, server);
        Assert.Equal(string.Empty, (await AuthinfoAsync(server)).Cap);
    }

    [Fact]
    public async Task SAsksForTheNvramsKeyWhenTheHostHasNoneAndAddsItThoughItCannotBeKept()
    {
        console.Type("bootes\nfog\n\nbootespw\nbootespw\n\n");
        await StartAsync("/tmp/a", "-S");
        Assert.Equal("key proto=dp9ik user=bootes dom=fog !hex? !password?\n", await CtlAsync("/tmp/a"));
    }

    [Fact]
    public async Task ACsThatNeverAnswersIsGivenUpOnAfterThirtySecondsForTheAuthAddressGiven()
    {
        await ServeAsync();
        init.Notify(note => note == "sys: write on closed pipe");
        (int posted, int held) = await init.PipeAsync();
        int srv = await init.CreateAsync("/srv/cs", NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(srv, Encoding.UTF8.GetBytes(posted.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await init.CloseAsync(srv);
        await init.CloseAsync(posted);
        await StartAsync("/tmp/a", "-a", AuthAddr);
        await KeyAsync("/tmp/a", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv login = await ConvAsync("/tmp/a", "proto=dp9ik role=login");
        await login.CallAsync("write", "bob"u8.ToArray());
        Task<(string Status, byte[] Data)> written = login.CallAsync("write", "bobpassw"u8.ToArray());
        while (!written.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        Assert.Equal("ok", (await written).Status);

        // Nothing holds cs's pipe once /srv lets go of it.
        await init.RemoveAsync("/srv/cs");
        await Assert.ThrowsAsync<SyscallException>(() => init.WriteAsync(held, "x"u8.ToArray()).AsTask());
    }

    [Fact]
    public async Task TheAuthServerIsFoundByCsInTheNamespaceOrPostedInSrv()
    {
        await ServeAsync();
        await WriteFileAsync("/tmp/ndb", "authdom=fog auth=127.0.0.1\ntcp=ticket port=17567\n");

        // cs mounted on /net, with its /srv file gone.
        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        await init.RemoveAsync("/srv/cs");
        await StartAsync("/tmp/a");
        Assert.Equal("ok", await LoginAsync("/tmp/a", "bob", "bobpassw"));

        // cs posted in /srv only, which factotum mounts.
        await init.UnmountAsync(null, "/net");
        await init.BindAsync("#I", "/net", NinePSharp.Namespaces.MountFlags.After);
        init.Fork(RforkFlags.Proc | RforkFlags.Nameg, child => child.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        await StartAsync("/tmp/b");
        Assert.Equal("ok", await LoginAsync("/tmp/b", "bob", "bobpassw"));
    }

    [Fact]
    public async Task WithoutTheNetworkInNetFactotumBindsItThere()
    {
        await ServeAsync();
        await init.CloseAsync(await init.CreateAsync("/tmp/nonet", NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        await StartInAsync(c => c.BindAsync("/tmp/nonet", "/net", NinePSharp.Namespaces.MountFlags.Replace).AsTask(), "/tmp/a", "-a", AuthAddr);
        Assert.Equal("ok", await LoginAsync("/tmp/a", "bob", "bobpassw"));
    }

    [Fact]
    public async Task AnAuthAddressIsAHostOfPort567AndEachIsTriedInTurn()
    {
        await ServeAsync();
        await StartAsync("/tmp/a", "-a", "127.0.0.9", "-a", "127.0.0.1");
        Assert.Equal("ok", await LoginAsync("/tmp/a", "bob", "bobpassw"));

        // One that cannot be called fails the login as the last call, by il, did.
        string why = (await Assert.ThrowsAsync<SyscallException>(() => Net.DialAsync(init, "il!127.0.0.9!566"))).Message;
        await StartAsync("/tmp/b", "-a", "127.0.0.9");
        await KeyAsync("/tmp/b", "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv login = await ConvAsync("/tmp/b", "proto=dp9ik role=login");
        await login.CallAsync("write", "bob"u8.ToArray());
        Assert.Equal(("error", why), await login.TextAsync("write", "bobpassw"u8.ToArray()));
    }

    [Fact]
    public async Task WithoutCsOrAnAuthAddressTheAuthServersAreThoseNetNdbNamesAndASevenAtMost()
    {
        await ServeAsync();
        (string Ndb, bool Found)[] cases =
        [
            ("auth=127.0.0.1\n", true),
            ("sys=x\tauth=127.0.0.1 ip=1\n", true),
            ("auth= auth=127.0.0.9 auth=127.0.0.1\n", true),
            ("xauth=127.0.0.1\n", false),
            ("auth=127.0.0.1", false),
            (string.Concat(Enumerable.Range(2, 6).Select(i => $"auth=127.0.0.{i} ")) + "auth=127.0.0.1\n", true),
            (string.Concat(Enumerable.Range(2, 7).Select(i => $"auth=127.0.0.{i} ")) + "auth=127.0.0.1\n", false),
        ];
        int n = 0;
        foreach ((string ndb, bool found) in cases)
        {
            string dir = $"/tmp/netx{n}", mtpt = $"/tmp/f{n++}";
            await init.CloseAsync(await init.CreateAsync(dir, NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
            await WriteFileAsync($"{dir}/ndb", ndb);
            await StartInAsync(c => c.BindAsync(dir, "/net", NinePSharp.Namespaces.MountFlags.Before).AsTask(), mtpt);
            Assert.Equal(found ? "ok" : "error", await LoginAsync(mtpt, "bob", "bobpassw"));
        }
    }

    private static Authkey Key(string password, string user)
    {
        Authkey k = Authkey.Passtokey(password);
        k.Hash(user);
        return k;
    }

    private static byte[] Flipped(byte[] b) => [.. b.Select(x => (byte)(x ^ 1))];

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int n)
    {
        byte[] b = new byte[n];
        await stream.ReadExactlyAsync(b);
        return b;
    }

    private static byte[] Form0(byte[] tickets) => [.. Enumerable.Repeat((byte)'x', 72), .. tickets[124..]];

    private static async Task<byte[]> PakreqAsync(Conv client, Conv server)
    {
        byte[] chal = (await client.CallAsync("read")).Data;
        Assert.Equal("ok", (await server.CallAsync("write", chal)).Status);
        return (await server.CallAsync("read")).Data;
    }

    private static async Task ToTicketAsync(Conv client, Conv server)
    {
        Assert.Equal("ok", (await client.CallAsync("write", await PakreqAsync(client, server))).Status);
        Assert.Equal("ok", (await server.CallAsync("write", (await client.CallAsync("read")).Data)).Status);
    }

    // The client and server's dp9ik run to its end.
    private static async Task ExchangeAsync(Conv client, Conv server)
    {
        await ToTicketAsync(client, server);
        Assert.Equal("ok", (await server.CallAsync("write", (await client.CallAsync("read")).Data)).Status);
        (string status, byte[] auth) = await server.CallAsync("read");
        Assert.Equal(("ok", 68), (status, auth.Length));
        Assert.Equal("ok", (await client.CallAsync("write", auth)).Status);
    }

    // authinfo's users, capability and secret, each after its length.
    private static async Task<(string Cuid, string Suid, string Cap, byte[] Secret)> AuthinfoAsync(Conv c)
    {
        (string status, byte[] a) = await c.CallAsync("authinfo");
        Assert.Equal("ok", status);
        string[] s = new string[3];
        int p = 0;
        for (int i = 0; i < 3; i++)
        {
            int n = BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(p));
            s[i] = Encoding.UTF8.GetString(a, p + 2, n);
            p += 2 + n;
        }

        int ns = BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(p));
        Assert.Equal(p + 2 + ns, a.Length);

        // A read too small is refused, and the process that refused it serves on.
        long[] serving = [.. c.Process.Kernel.Processes.Keys.Order()];
        Assert.Equal(("error", "read too small"), await c.TextAsync("authinfo", count: a.Length + 3));
        await Task.Delay(50);
        Assert.Equal(serving, c.Process.Kernel.Processes.Keys.Order());
        return (s[0], s[1], s[2], a[(p + 2)..]);
    }

    // The test as the client and auth server both, to a server conversation: the ticket and
    // authenticator it makes are as asked, and the server's answer, which must open, is checked.
    private async Task<(string Status, string Text)> TicketToServerAsync(byte form = 1, byte num = Authsrv.AuthTs, bool chal = false, byte authNum = Authsrv.AuthAc, bool authChal = false)
    {
        Conv server = await ConvAsync("/tmp/a", Server);
        byte[] cchal = "cchallen"u8.ToArray();
        await server.CallAsync("write", cchal);
        byte[] pakreq = (await server.CallAsync("read")).Data;
        var tr = new Ticketreq();
        Authsrv.ConvM2TR(pakreq, Authsrv.Tickreqlen, tr);
        Authkey ka = Key("glendapw", "glenda");
        byte[] ya = Authsrv.AuthpakNew(out Dp9ik.AuthPakState pa, ka, false);
        Assert.Equal(0, Authsrv.AuthpakFinish(pa, ka, pakreq.AsSpan(Authsrv.Tickreqlen)));
        Assert.Equal("ok", (await server.CallAsync("write", ya)).Status);

        var t = new Ticket { Num = num, Form = form };
        (chal ? Flipped(tr.Chal) : tr.Chal).CopyTo(t.Chal, 0);
        Authsrv.Safecpy(t.Cuid, "glenda");
        Authsrv.Safecpy(t.Suid, "glenda");
        t.Key.AsSpan().Fill(9);
        var a = new Authenticator { Num = authNum };
        (authChal ? Flipped(tr.Chal) : tr.Chal).CopyTo(a.Chal, 0);
        a.Rand.AsSpan().Fill(6);
        (string Status, string Text) reply = await server.TextAsync("write", [.. Authsrv.ConvT2M(t, ka), .. Authsrv.ConvA2M(a, t)]);
        if (reply.Status == "ok")
        {
            byte[] sealedAs = (await server.CallAsync("read")).Data;
            var back = new Authenticator();
            Assert.Equal(Authsrv.Maxauthentlen, Authsrv.ConvM2A(sealedAs, sealedAs.Length, back, t));
            Assert.Equal(Authsrv.AuthAs, back.Num);
            Assert.Equal(cchal, back.Chal);
            Assert.Equal(Dp9ik.SessionSecret.Derive(a.Rand, back.Rand, t.Key), (await AuthinfoAsync(server)).Secret);
        }

        return reply;
    }

    // The test as the server to a client conversation, with no auth server, so the client makes up
    // the tickets: its authenticator is opened, and the server's made as asked.
    private async Task<((string Status, string Text) Reply, Conv Client, byte[] Secret)> AuthenticatorToClientAsync(byte num, bool badChal)
    {
        Conv client = await ConvAsync("/tmp/a", Client);
        byte[] cchal = (await client.CallAsync("read")).Data;
        var tr = new Ticketreq { Type = Authsrv.AuthPAK };
        Authsrv.Safecpy(tr.Authid, "glenda");
        Authsrv.Safecpy(tr.Authdom, "fog");
        "schallen"u8.CopyTo(tr.Chal);
        Authkey ks = Key("glendapw", "glenda");
        byte[] ys = Authsrv.AuthpakNew(out Dp9ik.AuthPakState p, ks, true);
        Assert.Equal("ok", (await client.CallAsync("write", [.. Authsrv.ConvTR2M(tr), .. ys])).Status);
        Assert.Equal(0, Authsrv.AuthpakFinish(p, ks, (await client.CallAsync("read")).Data));
        byte[] tickets = (await client.CallAsync("read")).Data;
        var t = new Ticket();
        Assert.Equal(Authsrv.Maxticketlen, Authsrv.ConvM2T(tickets, tickets.Length, t, ks));
        Assert.Equal(Authsrv.AuthTs, t.Num);
        var ac = new Authenticator();
        Authsrv.ConvM2A(tickets[Authsrv.Maxticketlen..], Authsrv.Maxauthentlen, ac, t);
        Assert.Equal((Authsrv.AuthAc, "schallen"), (ac.Num, Encoding.UTF8.GetString(ac.Chal)));
        var a = new Authenticator { Num = num };
        (badChal ? Flipped(cchal) : cchal).CopyTo(a.Chal, 0);
        a.Rand.AsSpan().Fill(8);
        (string, string) reply = await client.TextAsync("write", Authsrv.ConvA2M(a, t));
        return (reply, client, Dp9ik.SessionSecret.Derive(ac.Rand, a.Rand, t.Key));
    }

    // An auth server that gives what the script says, for keys of the test's passwords.
    private async Task ScriptAsync(Script script)
    {
        listener?.Stop();
        listener = new TcpListener(IPAddress.Loopback, host.HostPort(17567));
        listener.Start();
        TcpListener listening = listener;
        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                using Socket socket = await listening.AcceptSocketAsync(stopping.Token);
                using var stream = new NetworkStream(socket);
                try
                {
                    var tr = new Ticketreq();
                    Authsrv.ConvM2TR(await ReadExactlyAsync(stream, Authsrv.Tickreqlen), Authsrv.Tickreqlen, tr);
                    byte[] ys = await ReadExactlyAsync(stream, Authsrv.Pakylen), yc = await ReadExactlyAsync(stream, Authsrv.Pakylen);
                    if (script.Refuse)
                    {
                        byte[] error = new byte[64];
                        "refused"u8.CopyTo(error);
                        await stream.WriteAsync((byte[])[Authsrv.AuthErr, .. error]);
                        continue;
                    }

                    Authkey ka = Key(passwords[Authsrv.Str(tr.Authid)], Authsrv.Str(tr.Authid)), kh = Key(passwords[Authsrv.Str(tr.Hostid)], Authsrv.Str(tr.Hostid));
                    byte[] ya = Authsrv.AuthpakNew(out Dp9ik.AuthPakState pa, ka, false), yh = Authsrv.AuthpakNew(out Dp9ik.AuthPakState ph, kh, false);
                    Authsrv.AuthpakFinish(pa, ka, ys);
                    Authsrv.AuthpakFinish(ph, kh, yc);
                    await stream.WriteAsync((byte[])[Authsrv.AuthOK, .. script.BadY ? Enumerable.Repeat((byte)0xff, Authsrv.Pakylen) : ya, .. script.BadHostY ? Enumerable.Repeat((byte)0xff, Authsrv.Pakylen) : yh]);
                    if (script.BadHostY)
                    {
                        continue;
                    }

                    await ReadExactlyAsync(stream, Authsrv.Tickreqlen);
                    var tc = new Ticket { Num = script.TcNum, Form = script.Form };
                    var ts = new Ticket { Num = script.TsNum, Form = script.TsForm ?? script.Form };
                    (script.TcChal ? Flipped(tr.Chal) : tr.Chal).CopyTo(tc.Chal, 0);
                    (script.TsChal ? Flipped(tr.Chal) : tr.Chal).CopyTo(ts.Chal, 0);
                    foreach (Ticket t in new[] { tc, ts })
                    {
                        tr.Uid.CopyTo(t.Cuid, 0);
                        tr.Uid.CopyTo(t.Suid, 0);
                        t.Key.AsSpan().Fill(5);
                    }

                    ts.Key[0] ^= (byte)(script.TsKey ? 1 : 0);
                    await stream.WriteAsync((byte[])[Authsrv.AuthOK, .. Authsrv.ConvT2M(tc, kh), .. Authsrv.ConvT2M(ts, ka)]);
                }
                catch (Exception error) when (error is IOException or EndOfStreamException)
                {
                }
            }
        });
        await Task.CompletedTask;
    }

    // A login of the user, with the host's key glenda's, and what its password's write is answered.
    private async Task<string> LoginAsync(string mtpt, string user, string password)
    {
        await KeyAsync(mtpt, "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        Conv login = await ConvAsync(mtpt, "proto=dp9ik role=login");
        await login.CallAsync("write", Encoding.UTF8.GetBytes(user));
        return (await login.CallAsync("write", Encoding.UTF8.GetBytes(password))).Status;
    }

    private async Task WriteFileAsync(string path, string text)
    {
        int f = await init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(f, Encoding.UTF8.GetBytes(text));
        await init.CloseAsync(f);
    }

    // factotum started in a namespace of its own, which prepare makes, and mounted from /srv on the
    // mount point in init's.
    private async Task StartInAsync(Func<Process, Task> prepare, string mtpt, params string[] arguments)
    {
        await init.CloseAsync(await init.CreateAsync(mtpt, NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg, async child =>
        {
            await prepare(child);
            await child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-p", "-s", $"factotum.{mtpt[5..]}", .. arguments]);
        });
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        await init.MountAsync(await init.OpenAsync($"/srv/factotum.{mtpt[5..]}", NinePConstants.ORDWR), -1, mtpt, NinePSharp.Namespaces.MountFlags.Replace, string.Empty);
    }

    // factotum started on the mount point, its files posted as /srv/factotum.name for the mount point's name.
    private async Task StartAsync(string mtpt, params string[] arguments)
    {
        await init.CloseAsync(await init.CreateAsync(mtpt, NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-p", "-m", mtpt, "-s", $"factotum.{mtpt[5..]}", .. arguments]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
    }

    private async Task KeyAsync(string mtpt, string key)
    {
        int ctl = await init.OpenAsync($"{mtpt}/factotum/ctl", NinePConstants.OWRITE);
        await init.WriteAsync(ctl, Encoding.UTF8.GetBytes(key)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await init.CloseAsync(ctl);
    }

    private async Task<string> KeyErrorAsync(string mtpt, string key)
    {
        int ctl = await init.OpenAsync($"{mtpt}/factotum/ctl", NinePConstants.OWRITE);
        string error = (await Assert.ThrowsAsync<SyscallException>(() => init.WriteAsync(ctl, Encoding.UTF8.GetBytes(key)).AsTask())).Message;
        await init.CloseAsync(ctl);
        return error;
    }

    private async Task<string> CtlAsync(string mtpt)
    {
        int ctl = await init.OpenAsync($"{mtpt}/factotum/ctl", NinePConstants.OREAD);
        var text = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            ReadOnlyMemory<byte> line = await init.ReadAsync(ctl, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (line.IsEmpty)
            {
                break;
            }

            text.Append(Encoding.UTF8.GetString(line.Span));
        }

        await init.CloseAsync(ctl);
        return text.ToString();
    }

    // An rpc conversation of the user's, started with the attributes: another user's own process
    // mounts factotum, which then knows it as that user.
    private async Task<Conv> ConvAsync(string mtpt, string attr, string user = "glenda", bool start = true)
    {
        Process p = init;
        if (user != init.User)
        {
            var made = new TaskCompletionSource<Process>();
            init.Fork(RforkFlags.Proc | RforkFlags.Nameg, async c =>
            {
                int u = await c.OpenAsync("/dev/user", NinePConstants.OWRITE);
                await c.WriteAsync(u, Encoding.UTF8.GetBytes(user));
                await c.MountAsync(await c.OpenAsync($"/srv/factotum.{mtpt[5..]}", NinePConstants.ORDWR), -1, mtpt, NinePSharp.Namespaces.MountFlags.Replace, string.Empty);
                made.SetResult(c);
                await Task.Delay(Timeout.Infinite, stopping.Token);
            });
            p = await made.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        int fd = await p.OpenAsync($"{mtpt}/factotum/rpc", NinePConstants.ORDWR);
        var c = new Conv(p, fd);
        if (start)
        {
            Assert.Equal("ok", (await c.CallAsync("start", Encoding.UTF8.GetBytes(attr))).Status);
        }

        return c;
    }

    // authsrv on the port the -a address names, with the passwords and speaks-for rules the test gives.
    private Task ServeAsync()
    {
        listener = new TcpListener(IPAddress.Loopback, host.HostPort(17567));
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                Socket socket = await listener.AcceptSocketAsync(stopping.Token);
                Interlocked.Increment(ref served);
                _ = Task.Run(async () =>
                {
                    using var stream = new NetworkStream(socket, ownsSocket: true);
                    try
                    {
                        await new AuthServerConnection(stream, new Database(passwords), "127.0.0.1", rules).ServeAsync(stopping.Token);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or EndOfStreamException)
                    {
                    }
                });
            }
        });
        return Task.CompletedTask;
    }

    // What a scripted auth server gives: tickets of the form, number, challenge and key asked, and
    // a good AuthPAK value for the server's key or not.
    private sealed class Script
    {
        public byte Form { get; init; } = 1;

        public byte? TsForm { get; init; }

        public bool BadY { get; init; }

        public bool BadHostY { get; init; }

        public bool Refuse { get; init; }

        public byte TcNum { get; init; } = Authsrv.AuthTc;

        public byte TsNum { get; init; } = Authsrv.AuthTs;

        public bool TcChal { get; init; }

        public bool TsChal { get; init; }

        public bool TsKey { get; init; }
    }

    // An rpc file open for reading and writing.
    private sealed class Conv(Process process, int fd)
    {
        public int Fd => fd;

        public Process Process => process;

        // The verb and its argument written, and the answer read: its first word, and what follows.
        public async Task<(string Status, byte[] Data)> CallAsync(string verb, byte[]? arg = null, int count = 8192)
        {
            await process.WriteAsync(fd, arg is null ? Encoding.UTF8.GetBytes(verb) : [.. Encoding.UTF8.GetBytes(verb + " "), .. arg]);
            byte[] r = (await process.ReadAsync(fd, count).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).ToArray();
            int space = Array.IndexOf(r, (byte)' ');
            return space < 0 ? (Encoding.UTF8.GetString(r), []) : (Encoding.UTF8.GetString(r, 0, space), r[(space + 1)..]);
        }

        public async Task<(string Status, string Text)> TextAsync(string verb, byte[]? arg = null, int count = 8192)
        {
            (string status, byte[] data) = await CallAsync(verb, arg, count);
            return (status, Encoding.UTF8.GetString(data));
        }
    }

    // keyfs as authsrv sees it: each user's key from a password, hashed for the user.
    private sealed class Database(Dictionary<string, string> passwords) : IAuthDatabase
    {
        public Dp9ik.AuthKey? FindKey(string user)
        {
            if (!passwords.TryGetValue(user, out string? password))
            {
                return null;
            }

            Dp9ik.AuthKey key = Dp9ik.AuthKey.FromPassword(password);
            key.ApplyAuthPakHash(user);
            return key;
        }

        public bool SetKey(string user, Dp9ik.AuthKey key) => false;

        public bool SetSecret(string user, string secret) => false;

        public void Succeed(string user)
        {
        }
    }
}
