using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Fog.Namespaces.Tests.Support;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using NinePSharp.Namespaces.Authorization;
using NinePSharp.Namespaces.Orleans;
using NinePSharp.Namespaces.Orleans.Server;
using NinePSharp.Parser;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Namespaces.Tests.Steps;

[Binding]
public static class FogNamespaceHooks
{
    [BeforeTestRun]
    public static void StartCluster() => FogNamespaceCluster.Start();

    [AfterTestRun]
    public static void StopCluster() => FogNamespaceCluster.Stop();
}

[Binding]
[Scope(Feature = "Enrolled nodes attach to their own copy of the shared namespace")]
public sealed class NamespaceViewSteps : IAsyncDisposable
{
    private readonly IGrainFactory grains = FogNamespaceCluster.Cluster.GrainFactory;
    private readonly Dictionary<string, ResourceHandle> applications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, X509Certificate2> certificates = new(StringComparer.Ordinal);
    private readonly List<GroupMembership> memberships = new();
    private readonly List<ResourceGrant> grants = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly ManualTime time = new();
    private FogNamespaceLimits limits = new(Sessions: 8, FidsPerSession: 32, RequestsPerSession: 8, MessageSize: 8192,
        SessionLifetime: TimeSpan.FromMinutes(10));
    private FogSharedRoot? root;
    private FogNodePolicy? nodes;
    private FogAuthorizationAuthority? authority;
    private RecordingResolver? attaches;
    private BoundedNamespaceExport? export;
    private FogNodeListener? listener;
    private X509Certificate2? serverCertificate;
    private object? response;
    private Rwalk? walk;
    private int walkLength;
    private string? text;
    private IReadOnlyList<string>? names;
    private uint retainedFid;
    private string retainedUser = string.Empty;

    [Given(@"^a shared root with applications ""([^""]*)"" and ""([^""]*)"" mounted under /mnt$")]
    public async Task GivenSharedRoot(string first, string second)
    {
        root = await FogSharedRoot.CreateAsync(grains, $"root-{Guid.NewGuid():N}");
        await MountApplication(first, "inbox", "hello mail");
        await MountApplication(second, "todo", "remember");
    }

    [Given(@"^the shared root also contains ""([^""]*)""$")]
    public async Task GivenEntry(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 1; index <= parts.Length; index++)
            await root!.CreateEntryAsync("/" + string.Join('/', parts.Take(index)), directory: index < parts.Length);
    }

    [Given(@"^enrolled nodes ""([^""]*)"" and ""([^""]*)""$")]
    public void GivenNodes(string first, string second)
    {
        foreach (string name in new[] { first, second, "stranger" }) certificates[name] = FogNamespaceCluster.Certificate($"{name}.test");
        nodes = new FogNodePolicy(1, new[] { first, second }.Select(name =>
            new FogNodeEnrollment(name, new string('1', 64), FogNodePolicy.SpkiPin(certificates[name]), $"{name}.test")));
    }

    [Given(@"^group ""([^""]*)"" has members ""([^""]*)""$")]
    public void GivenGroup(string group, string members)
    {
        foreach (string member in members.Split(',', StringSplitOptions.TrimEntries))
            memberships.Add(new GroupMembership(group, member));
    }

    [Given(@"^the policy generation is (\d+) with grants$")]
    public async Task GivenPolicy(ulong generation, DataTable table)
    {
        foreach (DataTableRow row in table.Rows)
        {
            grants.Add(new ResourceGrant(
                Enum.Parse<GrantSubjectKind>(row["kind"], ignoreCase: true),
                row["subject"],
                (await root!.ResolveAsync(row["path"])).Identity,
                Enum.Parse<GrantScope>(row["scope"], ignoreCase: true),
                Enum.Parse<ResourceRights>(row["rights"].Replace(",", ", ", StringComparison.Ordinal), ignoreCase: true)));
        }

        authority = new FogAuthorizationAuthority(Policy(generation));
    }

    [Given(@"^the export admits at most (\d+) sessions?$")]
    public void GivenSessionLimit(int count) => limits = limits with { Sessions = count };

    [Given(@"^the export admits at most (\d+) fids per session$")]
    public void GivenFidLimit(int count) => limits = limits with { FidsPerSession = count };

    [Given(@"^""(\w+)"" has attached$")]
    [When(@"^""(\w+)"" attaches$")]
    public Task Attaches(string user) => AttachAs(user, user, user, 8192, expectSuccess: true);

    [Given(@"^""(\w+)"" and ""(\w+)"" have attached$")]
    public async Task BothAttached(string first, string second)
    {
        await Attaches(first);
        await Attaches(second);
    }

    [Given(@"^""(\w+)"" has attached with message size (\d+)$")]
    public Task AttachedWithSize(string user, uint size) => AttachAs(user, user, user, size, expectSuccess: true);

    [When(@"^""(\w+)"" attaches again$")]
    public Task AttachesAgain(string user) => AttachAs(user, user, user, 8192, expectSuccess: true);

    [When(@"^""(\w+)"" attaches with attach name ""([^""]*)""$")]
    public Task AttachesWithName(string user, string aname)
        => AttachAs($"{user}-aname", user, user, 8192, expectSuccess: false, aname);

    [When(@"^the certificate of ""(\w+)"" attaches as ""(\w+)""$")]
    public Task CertificateAttachesAs(string certificate, string uname)
        => AttachAs($"{certificate}-as-{uname}", certificate, uname, 8192, expectSuccess: false);

    [When(@"^""(\w+)"" negotiates a version$")]
    public async Task NegotiatesVersion(string user)
    {
        sessions[user] = new Session(user, $"{user}-{Guid.NewGuid():N}");
        response = await Send(user, NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000")));
    }

    [Then("the two attaches own distinct process groups")]
    public void DistinctGroups()
    {
        Assert.Equal(2, attaches!.Descriptors.Count);
        string[] groups = attaches.Descriptors.Select(descriptor => descriptor.ProcessGroupId).ToArray();
        Assert.Equal(2, groups.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(root!.ProcessGroupId, groups);
    }

    [Then("each process group mounts the same applications as the shared root")]
    public async Task SameMounts()
    {
        string expected = Mounts(await grains.GetGrain<IVProcessGroupGrain>(root!.ProcessGroupId).GetSnapshotAsync());
        Assert.Contains(applications["mail"].Identity.Device, expected, StringComparison.Ordinal);
        foreach (DistributedNamespaceAttach descriptor in attaches!.Descriptors)
            Assert.Equal(expected, Mounts(await grains.GetGrain<IVProcessGroupGrain>(descriptor.ProcessGroupId).GetSnapshotAsync()));
    }

    [When(@"^""(\w+)"" mounts ""([^""]*)"" on ""([^""]*)"" in its own namespace$")]
    public async Task MountsInOwnNamespace(string user, string application, string mountPoint)
    {
        ResourceHandle target = applications[application.Split('/')[^1]];
        ResourceHandle point = await root!.ResolveAsync(mountPoint);
        await grains.GetGrain<IVProcessGroupGrain>(GroupOf(user))
            .MountAsync(target.ToModel(), point.ToModel(), MountFlags.Replace);
    }

    [Then(@"^the shared root still mounts ""(\w+)"" at ""([^""]*)""$")]
    public Task SharedRootStillMounts(string application, string mountPoint)
        => AssertMount(root!.ProcessGroupId, application, mountPoint);

    [Then(@"^the namespace of ""(\w+)"" still mounts ""(\w+)"" at ""([^""]*)""$")]
    public Task OtherStillMounts(string user, string application, string mountPoint)
        => AssertMount(GroupOf(user), application, mountPoint);

    [When(@"^""(\w+)"" reads ""([^""]*)""$")]
    public async Task Reads(string user, string path) => text = await ReadFile(user, path);

    [Then(@"^the read returns ""([^""]*)""$")]
    public void ReadReturns(string expected) => Assert.Equal(expected, text);

    [Then(@"^""(\w+)"" reads ""([^""]*)"" as ""([^""]*)""$")]
    public async Task ReadsAs(string user, string path, string expected) => Assert.Equal(expected, await ReadFile(user, path));

    [When(@"^""(\w+)"" walks to ""([^""]*)""$")]
    public async Task WalksTo(string user, string path)
    {
        string[] parts = Parts(path);
        walkLength = parts.Length;
        response = await Send(user, NinePMessage.NewMsgTwalk(new Twalk(0, 1, sessions[user].NextFid(), parts)));
        walk = response as Rwalk?;
    }

    [When(@"^""(\w+)"" walks to ""([^""]*)"" into (?:a|another) new fid$")]
    public Task WalksIntoNewFid(string user, string path) => WalksTo(user, path);

    [When(@"^""(\w+)"" walks ""\.\."", ""\.\."", ""\.\."" from its root$")]
    public async Task WalksUp(string user)
    {
        walkLength = 3;
        response = await Send(user, NinePMessage.NewMsgTwalk(new Twalk(0, 1, sessions[user].NextFid(), ["..", "..", ".."])));
        walk = Assert.IsType<Rwalk>(response);
    }

    [Then("the walk reports not found")]
    public void WalkNotFound()
        => Assert.True(response is Rerror || (walk is { } partial && partial.Wqid.Length < walkLength),
            $"expected a failed walk, got {Describe(response)}");

    [Then("the walk ends at the shared root's directory")]
    public void WalkEndsAtRoot()
    {
        Rwalk result = Assert.IsType<Rwalk>(response);
        Assert.Equal(3, result.Wqid.Length);
        Assert.All(result.Wqid, qid => Assert.Equal(sessions.Values.First().RootQid.Path, qid.Path));
    }

    [When(@"^""(\w+)"" lists ""([^""]*)""$")]
    [When(@"^""(\w+)"" opens ""([^""]*)"" and reads its stat records$")]
    public async Task Lists(string user, string path)
    {
        uint fid = await OpenPath(user, path, NinePConstants.OREAD);
        var records = new List<string>();
        for (ulong offset = 0; ;)
        {
            Rread read = Assert.IsType<Rread>(await Send(user, NinePMessage.NewMsgTread(new Tread(0, fid, offset, 4096))));
            if (read.Count == 0) break;
            for (int position = 0; position < read.Data.Length;)
                records.Add(new Stat(read.Data.Span, ref position).Name);
            offset += read.Count;
        }

        await Send(user, NinePMessage.NewMsgTclunk(new Tclunk(0, fid)));
        names = records;
    }

    [Then(@"^the (?:listing contains|records name) exactly ""([^""]*)""$")]
    public void ListingIs(string expected)
        => Assert.Equal(expected.Split(',', StringSplitOptions.TrimEntries), names);

    [When(@"^""(\w+)"" writes ""([^""]*)"" to ""([^""]*)""$")]
    public Task Writes(string user, string data, string path) => WriteFile(user, path, Encoding.ASCII.GetBytes(data));

    [When(@"^""(\w+)"" writes (\d+) bytes to ""([^""]*)""$")]
    public Task WritesBytes(string user, int count, string path) => WriteFile(user, path, new byte[count]);

    [Then(@"^the (mail|notes) application recorded (one|no) writes?$")]
    public async Task Recorded(string application, string count)
        => Assert.Equal(count == "one" ? 1 : 0,
            await grains.GetGrain<ITestApplicationGrain>(applications[application].Identity.Device).GetWritesAsync());

    [Then(@"^the request fails with ""([^""]*)""$")]
    public void RequestFails(string message)
        => Assert.Equal(message, Assert.IsType<Rerror>(response).Ename);

    [Then("the attach fails")]
    public void AttachFails() => Assert.IsType<Rerror>(response);

    [Given(@"^""(\w+)"" has opened ""([^""]*)"" for reading$")]
    public async Task HasOpened(string user, string path)
    {
        retainedFid = await OpenPath(user, path, NinePConstants.OREAD);
        retainedUser = user;
    }

    [When("the host replaces the policy with generation 2 and the same grants")]
    public void ReplacesPolicy() => authority!.Replace(Policy(2));

    [Then(@"^reading through that fid fails with ""([^""]*)""$")]
    public async Task ReadingRetainedFails(string message)
    {
        response = await Send(retainedUser, NinePMessage.NewMsgTread(new Tread(0, retainedFid, 0, 64)));
        RequestFails(message);
    }

    [When("the session lifetime elapses")]
    public void LifetimeElapses() => time.Advance(limits.SessionLifetime);

    [Given("the export behind the TLS node listener")]
    public void GivenListener()
    {
        serverCertificate = FogNamespaceCluster.Certificate("fog.test");
        listener = new FogNodeListener(new IPEndPoint(IPAddress.Loopback, 0), serverCertificate, nodes!, Export(),
            NullLogger.Instance, 4, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
        listener.Start();
    }

    [When(@"^""(\w+)"" connects with its certificate and reads ""([^""]*)""$")]
    public async Task ConnectsAndReads(string user, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var tls = await FogTlsClient.ConnectAsync(listener!.LocalEndpoint, "fog.test",
            FogNodePolicy.SpkiPin(serverCertificate!), certificates[user], timeout.Token);
        using var client = new NinePClient(tls);
        await client.VersionAsync(8192, "9P2000").WaitAsync(timeout.Token);
        await client.AttachAsync(1, NinePConstants.NoFid, user, "/").WaitAsync(timeout.Token);
        await client.WalkAsync(1, 2, Parts(path)).WaitAsync(timeout.Token);
        await client.OpenAsync(2, NinePConstants.OREAD).WaitAsync(timeout.Token);
        text = Encoding.ASCII.GetString((await client.ReadAsync(2, 0, 512).WaitAsync(timeout.Token)).Data.Span);
    }

    [Then("a connection without an enrolled certificate is rejected before any 9P request")]
    public async Task UnenrolledRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Exception? failure = null;
        try
        {
            await using var tls = await FogTlsClient.ConnectAsync(listener!.LocalEndpoint, "fog.test",
                FogNodePolicy.SpkiPin(serverCertificate!), certificates["stranger"], timeout.Token);
            using var client = new NinePClient(tls);
            await client.VersionAsync(8192, "9P2000").WaitAsync(timeout.Token);
        }
        catch (Exception caught)
        {
            failure = caught;
        }

        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (listener is not null) await listener.DisposeAsync();
        if (export is not null)
            foreach (Session session in sessions.Values) await export.CloseSessionAsync(session.Id);
        foreach (X509Certificate2 certificate in certificates.Values) certificate.Dispose();
        serverCertificate?.Dispose();
    }

    private async Task MountApplication(string name, string file, string contents)
    {
        string device = $"{name}-{Guid.NewGuid():N}";
        await grains.GetGrain<ITestApplicationGrain>(device).SeedAsync(file, contents);
        var applicationRoot = new ResourceHandle(new ResourceIdentity(TestApplicationGrain.Provider, device, 1), QidType.QTDIR);
        applications[name] = applicationRoot;
        await root!.MountApplicationAsync(name, applicationRoot);
    }

    private AuthorizationPolicy Policy(ulong generation)
        => new(generation, new[] { "worker", "other" }.Select(user => new AuthorizationPrincipal(user)), memberships, grants);

    private BoundedNamespaceExport Export()
    {
        if (export is not null) return export;
        var registered = new RegisteredMountableResourceResolver(grains,
        [
            ResourceProviderRegistration.For<IFogRootGrain>(FogSharedRoot.Provider),
            ResourceProviderRegistration.For<ITestApplicationGrain>(TestApplicationGrain.Provider),
        ]);
        var resources = new OrleansResourceOperations(registered);
        attaches = new RecordingResolver(new FogNamespaceAttachResolver(grains, root!, nodes!, authority!, resources,
            new OrleansResourceAncestry(registered)));
        var inner = new DistributedNamespaceDispatcher(new DistributedNamespaceOperations(grains, resources), attaches);
        export = new BoundedNamespaceExport(inner, limits, time);
        return export;
    }

    private async Task AttachAs(string sessionName, string certificate, string uname, uint messageSize, bool expectSuccess, string aname = "/")
    {
        var session = new Session(certificate, $"{sessionName}-{Guid.NewGuid():N}");
        sessions[sessionName] = session;
        response = await Send(sessionName, NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, messageSize, "9P2000")));
        if (response is not Rversion)
        {
            Assert.False(expectSuccess, $"version failed: {Describe(response)}");
            return;
        }

        response = await Send(sessionName, NinePMessage.NewMsgTattach(new Tattach(0, 1, NinePConstants.NoFid, uname, aname)));
        if (expectSuccess) session.RootQid = Assert.IsType<Rattach>(response).Qid;
    }

    private async Task<object> Send(string sessionName, NinePMessage message)
    {
        Session session = sessions[sessionName];
        return await Export().DispatchAsync(session.Id, session.Tagged(message), NinePDialect.NineP2000,
            certificates[session.Certificate]);
    }

    private async Task<uint> OpenPath(string user, string path, byte mode)
    {
        uint fid = sessions[user].NextFid();
        response = await Send(user, NinePMessage.NewMsgTwalk(new Twalk(0, 1, fid, Parts(path))));
        Assert.Equal(Parts(path).Length, Assert.IsType<Rwalk>(response).Wqid.Length);
        response = await Send(user, NinePMessage.NewMsgTopen(new Topen(0, fid, mode)));
        Assert.IsType<Ropen>(response);
        return fid;
    }

    private async Task<string> ReadFile(string user, string path)
    {
        uint fid = await OpenPath(user, path, NinePConstants.OREAD);
        Rread read = Assert.IsType<Rread>(await Send(user, NinePMessage.NewMsgTread(new Tread(0, fid, 0, 512))));
        await Send(user, NinePMessage.NewMsgTclunk(new Tclunk(0, fid)));
        return Encoding.ASCII.GetString(read.Data.Span);
    }

    private async Task WriteFile(string user, string path, byte[] data)
    {
        uint fid = sessions[user].NextFid();
        response = await Send(user, NinePMessage.NewMsgTwalk(new Twalk(0, 1, fid, Parts(path))));
        if (response is not Rwalk) return;
        response = await Send(user, NinePMessage.NewMsgTopen(new Topen(0, fid, (byte)(NinePConstants.OWRITE | NinePConstants.OTRUNC))));
        if (response is not Ropen) return;
        response = await Send(user, NinePMessage.NewMsgTwrite(new Twrite(0, fid, 0, data)));
        if (response is not Rwrite) return;
        await Send(user, NinePMessage.NewMsgTclunk(new Tclunk(0, fid)));
    }

    private async Task AssertMount(string group, string application, string mountPoint)
    {
        ResourceHandle point = await root!.ResolveAsync(mountPoint);
        MountHeadModel? head = await grains.GetGrain<IVProcessGroupGrain>(group).FindMountAsync(point.Identity.ToModel());
        Assert.NotNull(head);
        Assert.Equal(applications[application].Identity, Assert.Single(head.Mounts).Target.Identity.ToDomain());
    }

    private string GroupOf(string user)
        => attaches!.Descriptors.Single(descriptor => descriptor.User == user).ProcessGroupId;

    private static string Mounts(NamespaceSnapshotModel snapshot)
        => string.Join(";", snapshot.MountHeads
            .Select(head => $"{head.From.Identity.Device}/{head.From.Identity.Path}=" +
                string.Join(",", head.Mounts.Select(mount => $"{mount.Target.Identity.Device}/{mount.Target.Identity.Path}")))
            .Order(StringComparer.Ordinal));

    private static string[] Parts(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Describe(object? value) => value is Rerror error ? $"Rerror {error.Ename}" : value?.GetType().Name ?? "null";

    private sealed class Session(string certificate, string id)
    {
        private ushort tag;
        private uint fid = 1;

        internal string Certificate { get; } = certificate;
        internal string Id { get; } = id;
        internal Qid RootQid { get; set; }

        internal uint NextFid() => ++fid;

        internal NinePMessage Tagged(NinePMessage message)
        {
            ushort next = ++tag;
            return message switch
            {
                NinePMessage.MsgTattach m => NinePMessage.NewMsgTattach(new Tattach(next, m.Item.Fid, m.Item.Afid, m.Item.Uname, m.Item.Aname)),
                NinePMessage.MsgTwalk m => NinePMessage.NewMsgTwalk(new Twalk(next, m.Item.Fid, m.Item.NewFid, m.Item.Wname)),
                NinePMessage.MsgTopen m => NinePMessage.NewMsgTopen(new Topen(next, m.Item.Fid, m.Item.Mode)),
                NinePMessage.MsgTread m => NinePMessage.NewMsgTread(new Tread(next, m.Item.Fid, m.Item.Offset, m.Item.Count)),
                NinePMessage.MsgTwrite m => NinePMessage.NewMsgTwrite(new Twrite(next, m.Item.Fid, m.Item.Offset, m.Item.Data)),
                NinePMessage.MsgTclunk m => NinePMessage.NewMsgTclunk(new Tclunk(next, m.Item.Fid)),
                _ => message,
            };
        }
    }

    private sealed class RecordingResolver(IDistributedNamespaceAttachResolver inner) : IDistributedNamespaceAttachResolver
    {
        internal List<DistributedNamespaceAttach> Descriptors { get; } = new();

        public async ValueTask<DistributedNamespaceAttach> ResolveAsync(string sessionId, Tattach request, NinePDialect dialect,
            X509Certificate2? certificate, CancellationToken cancellationToken)
        {
            DistributedNamespaceAttach descriptor = await inner.ResolveAsync(sessionId, request, dialect, certificate, cancellationToken);
            Descriptors.Add(descriptor);
            return descriptor;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
