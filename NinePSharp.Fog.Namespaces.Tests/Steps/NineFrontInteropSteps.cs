using System.Net;
using System.Security.Cryptography.X509Certificates;
using Dp9ik;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Constants;
using NinePSharp.Fog.Auth;
using NinePSharp.Fog.Auth.Tests.Support;
using NinePSharp.Fog.Namespaces.Tests.Support;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using NinePSharp.Namespaces.Authorization;
using NinePSharp.Namespaces.Orleans;
using NinePSharp.Namespaces.Orleans.Server;
using NinePSharp.Parser;
using NinePSharp.Server;
using Reqnroll;
using Reqnroll.UnitTestProvider;
using Xunit;

namespace NinePSharp.Fog.Namespaces.Tests.Steps;

[Binding]
[Scope(Feature = "A stock 9front terminal logs in to Fog and mounts its namespace")]
public sealed class NineFrontInteropSteps(IUnitTestRuntimeProvider runtime)
{
    private readonly IGrainFactory grains = FogNamespaceCluster.Cluster.GrainFactory;
    private readonly Dictionary<string, string> devices = new(StringComparer.Ordinal);
    private readonly List<ResourceGrant> grants = new();
    private readonly List<string> attached = new();
    private readonly List<string> trace = new();
    private SoftwareTpm? tpm;
    private string directory = string.Empty;
    private KeyFsHost? keyfsHost;
    private KeyFsClient? admin;
    private AuthServerHost? authServer;
    private AuthFidOptions? identity;
    private FogSharedRoot? root;
    private FogUserListener? listener;
    private NineFrontTerminal? terminal;
    private string printed = string.Empty;

    [Given(@"^a Fog host for the auth domain ""(.*)"" as ""(.*)"" with keyfs users$")]
    public async Task GivenHost(string authDomain, string authId, Table table)
    {
        if (NineFrontTerminal.Unavailable is string reason)
        {
            runtime.TestIgnore(reason);
        }

        identity = new AuthFidOptions { AuthId = authId, AuthDomain = authDomain };
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
        }

        authServer = AuthServerHost.Start(new AuthServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, keyfsHost);
    }

    [Given(@"^the shared root mounts ""(.*)"" with ""(.*)"" reading ""(.*)"" and ""(.*)"" with ""(.*)"" reading ""(.*)""$")]
    public async Task GivenApplications(string first, string firstFile, string firstText, string second, string secondFile, string secondText)
    {
        root = await FogSharedRoot.CreateAsync(grains, $"root-{Guid.NewGuid():N}");
        await MountApplication(first, firstFile, firstText);
        await MountApplication(second, secondFile, secondText);
    }

    [Given(@"^""(.*)"" may read and write ""(.*)""$")]
    public async Task GivenGrant(string user, string path)
    {
        foreach (string directory in new[] { "/", "/mnt" })
        {
            grants.Add(await Grant(user, directory, GrantScope.Self, ResourceRights.Stat | ResourceRights.Walk | ResourceRights.Read));
        }

        grants.Add(await Grant(user, path, GrantScope.Tree, ResourceRights.Stat | ResourceRights.Walk | ResourceRights.Read | ResourceRights.Write));
    }

    [Given("a 9front terminal in Fog's auth domain")]
    public async Task GivenTerminal()
    {
        listener = new FogUserListener(new IPEndPoint(IPAddress.Loopback, 0), new Tracing(Export(), trace), NullLogger.Instance, 8, TimeSpan.FromMinutes(10));
        listener.Start();
        terminal = await NineFrontTerminal.BootAsync();

        // The live CD does not configure its interface and its /lib/ndb is read-only. cs and factotum
        // are started again so that both see the auth domain in their namespaces.
        await RunAsync("ip/ipconfig");
        await RunAsync("cp /lib/ndb/local /tmp/ndblocal");
        await RunAsync($"echo 'authdom={identity!.AuthDomain} auth=tcp!10.0.2.2!{authServer!.LocalEndPoint.Port}' >>/tmp/ndblocal");
        await RunAsync("bind /tmp/ndblocal /lib/ndb/local");
        await RunAsync("ndb/cs");
        await RunAsync("auth/factotum");
    }

    [Given(@"^the terminal's factotum holds the dp9ik key of ""(.*)"" with password ""(.*)""$")]
    public Task GivenKey(string user, string password)
        => RunAsync($"echo 'key proto=dp9ik dom={identity!.AuthDomain} user={user} !password={password}' >/mnt/factotum/ctl");

    [When(@"^the terminal runs srv for Fog's user listener on /n/fog$")]
    public Task WhenSrv() => RunAsync($"srv tcp!10.0.2.2!{listener!.LocalEndpoint.Port} fog /n/fog");

    [When(@"^the terminal starts srv for Fog's user listener on /n/fog$")]
    public async Task WhenSrvStarts()
    {
        await terminal!.SendLineAsync($"srv tcp!10.0.2.2!{listener!.LocalEndpoint.Port} fog /n/fog");
        printed = await terminal.ExpectAsync("user[", TimeSpan.FromSeconds(60));
    }

    [Then(@"^factotum asks for the dp9ik key of ""(.*)""$")]
    public void ThenFactotumAsks(string authDomain) => ThenPrints($"!Adding key: proto=dp9ik dom={authDomain}");

    [When(@"^the terminal answers factotum as ""(.*)"" with password ""(.*)""$")]
    public async Task WhenAnswerFactotum(string user, string password)
    {
        await terminal!.ExpectAsync("]: ", TimeSpan.FromSeconds(10));
        await terminal.SendLineAsync(user);
        await terminal.ExpectAsync("password: ", TimeSpan.FromSeconds(10));
        await terminal.SendLineAsync(password);
        printed = await terminal.ExpectAsync(NineFrontTerminal.Prompt, TimeSpan.FromSeconds(60));
    }

    [When(@"^the terminal runs ""(.*)""$")]
    public Task WhenRun(string command) => RunAsync(command);

    [When(@"^the terminal changes the password of ""(.*)"" in ""(.*)"" from ""(.*)"" to ""(.*)""$")]
    public async Task WhenPasswd(string user, string authDomain, string old, string changed)
    {
        await terminal!.SendLineAsync($"passwd {user}@{authDomain}");
        foreach ((string prompt, string answer) in new[]
        {
            ("Password: ", old), ("change Plan 9 Password? [y/n]: ", "y"), ("Password: ", changed),
            ("Confirm password: ", changed), ("change Inferno/POP secret? [y/n]: ", "n"),
        })
        {
            await terminal.ExpectAsync(prompt, TimeSpan.FromSeconds(30));
            await terminal.SendLineAsync(answer);
        }

        printed = await terminal.ExpectAsync(NineFrontTerminal.Prompt, TimeSpan.FromSeconds(30));
    }

    [Then(@"^the terminal prints ""(.*)""$")]
    public void ThenPrints(string text) => Assert.True(printed.Contains(text, StringComparison.Ordinal), Failure());

    [Then(@"^the terminal does not print ""(.*)""$")]
    public void ThenDoesNotPrint(string text) => Assert.False(printed.Contains(text, StringComparison.Ordinal), Failure());

    [Then(@"^the export attached ""(.*)""$")]
    public void ThenAttached(string user) => Assert.Equal([user], attached.Distinct());

    [Then("the export attached nobody")]
    public void ThenAttachedNobody() => Assert.Empty(attached);

    [Then(@"^the (mail|notes) application recorded one write$")]
    public async Task ThenOneWrite(string application)
        => Assert.Equal(1, await grains.GetGrain<ITestApplicationGrain>(devices[application]).GetWritesAsync());

    [Then(@"^the terminal's passwd exits without an error$")]
    public async Task ThenPasswdSucceeded()
    {
        Assert.DoesNotContain("passwd:", printed, StringComparison.Ordinal);
        Assert.Equal("status", (await terminal!.RunAsync("echo status $status")).Trim());
    }

    [Then(@"^the keyfs holds the keys of ""(.*)"" for ""(.*)""$")]
    public async Task ThenKeys(string password, string user)
    {
        AuthKey key = AuthKey.FromPassword(password);
        Assert.Equal(key.DesKey, await admin!.ReadAsync($"{user}/key"));
        Assert.Equal(key.AesKey, await admin.ReadAsync($"{user}/aeskey"));
    }

    [AfterScenario]
    public async Task CleanUpAsync()
    {
        if (terminal is not null)
        {
            await terminal.DisposeAsync();
        }

        if (listener is not null)
        {
            await listener.DisposeAsync();
        }

        if (authServer is not null)
        {
            await authServer.DisposeAsync();
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

    private async Task RunAsync(string command) => printed = await terminal!.RunAsync(command);

    private string Failure()
    {
        lock (trace)
        {
            return $"{terminal!.Transcript(3000)}\n9P at the user listener:\n{string.Join('\n', trace)}";
        }
    }

    private async Task MountApplication(string name, string file, string contents)
    {
        string device = $"{name}-{Guid.NewGuid():N}";
        await grains.GetGrain<ITestApplicationGrain>(device).SeedAsync(file, contents);
        devices[name] = device;
        await root!.MountApplicationAsync(name, new ResourceHandle(new ResourceIdentity(TestApplicationGrain.Provider, device, 1), QidType.QTDIR));
    }

    private async Task<ResourceGrant> Grant(string user, string path, GrantScope scope, ResourceRights rights)
        => new(GrantSubjectKind.User, user, (await root!.ResolveAsync(path)).Identity, scope, rights);

    private BoundedNamespaceExport Export()
    {
        var registered = new RegisteredMountableResourceResolver(
            grains,
            [
            ResourceProviderRegistration.For<IFogRootGrain>(FogSharedRoot.Provider),
            ResourceProviderRegistration.For<ITestApplicationGrain>(TestApplicationGrain.Provider),
        ]);
        var resources = new OrleansResourceOperations(registered);
        var users = grants.Select(grant => grant.Subject).Distinct().Select(user => new AuthorizationPrincipal(user));
        var authority = new FogAuthorizationAuthority(new AuthorizationPolicy(1, users, [], grants));
        AuthFidExport? afids = null;
        var resolver = new Recording(
            new FogNamespaceAttachResolver(
                grains,
                root!,
                new FogNodePolicy(1, []),
                authority,
                resources,
                new OrleansResourceAncestry(registered),
                (session, afid) => afids!.AuthenticatedUser(session, afid)),
            attached);
        afids = new AuthFidExport(new DistributedNamespaceDispatcher(new DistributedNamespaceOperations(grains, resources), resolver), keyfsHost!, identity!);
        return new BoundedNamespaceExport(afids, new FogNamespaceLimits(8, 64, 16, 8192, TimeSpan.FromMinutes(10)));
    }

    // The 9P the user listener carried, for a failure message.
    private sealed class Tracing(BoundedNamespaceExport inner, List<string> lines) : INinePFSDispatcher, INinePSessionLifecycle
    {
        public async Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
        {
            object reply = await inner.DispatchAsync(sessionId, message, dialect, certificate);
            lock (lines)
            {
                lines.Add($"{Describe(message)} -> {Describe(reply)}");
            }

            return reply;
        }

        public Task CloseSessionAsync(string sessionId) => inner.CloseSessionAsync(sessionId);

        private static string Describe(object value) => value switch
        {
            NinePMessage.MsgTread m => $"Tread fid {m.Item.Fid} count {m.Item.Count}",
            NinePMessage.MsgTwrite m => $"Twrite fid {m.Item.Fid} {m.Item.Data.Length} bytes",
            NinePMessage.MsgTauth m => $"Tauth afid {m.Item.Afid} uname '{m.Item.Uname}' aname '{m.Item.Aname}'",
            NinePMessage.MsgTattach m => $"Tattach fid {m.Item.Fid} afid {m.Item.Afid} uname '{m.Item.Uname}' aname '{m.Item.Aname}'",
            NinePMessage message => message.GetType().Name,
            Rread r => $"Rread {r.Data.Length} bytes",
            Rerror r => $"Rerror '{r.Ename}'",
            _ => value.GetType().Name,
        };
    }

    private sealed class Recording(IDistributedNamespaceAttachResolver inner, List<string> users) : IDistributedNamespaceAttachResolver
    {
        public async ValueTask<DistributedNamespaceAttach> ResolveAsync(
            string sessionId,
            Tattach request,
            NinePDialect dialect,
            X509Certificate2? certificate,
            CancellationToken cancellationToken)
        {
            DistributedNamespaceAttach descriptor = await inner.ResolveAsync(sessionId, request, dialect, certificate, cancellationToken);
            users.Add(descriptor.User);
            return descriptor;
        }
    }
}
