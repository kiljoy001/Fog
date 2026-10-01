using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Fog.Namespaces.Tests.Support;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using NinePSharp.Namespaces.Authorization;
using NinePSharp.Namespaces.Orleans;
using NinePSharp.Parser;
using Xunit;

namespace NinePSharp.Fog.Namespaces.Tests;

public sealed class FogNamespaceResolverTests
{
    private static readonly ResourceHandle Child = new(new ResourceIdentity("p", "d", 2), QidType.QTFILE);
    private static readonly ResourceHandle Parent = new(new ResourceIdentity("p", "d", 1), QidType.QTDIR);

    public FogNamespaceResolverTests() => FogNamespaceCluster.Start();

    private static IGrainFactory Grains => FogNamespaceCluster.Cluster.GrainFactory;

    [Fact]
    public async Task AttachesNeedNoAuthFidTheRootAttachNameAndAnEnrolledCertificate()
    {
        using X509Certificate2 certificate = FogNamespaceCluster.Certificate("worker.test");
        (FogNamespaceAttachResolver resolver, _) = await Resolver(certificate);
        async Task<FogException> Denied(Tattach request, X509Certificate2? presented)
            => await Assert.ThrowsAsync<FogException>(async () =>
                await resolver.ResolveAsync("s", request, NinePDialect.NineP2000, presented, CancellationToken.None));

        Assert.Equal("denied", (await Denied(new Tattach(1, 1, 0, "worker", "/"), certificate)).Code);
        Assert.Equal("denied", (await Denied(new Tattach(1, 1, NinePConstants.NoFid, "worker", "runtime"), certificate)).Code);
        Assert.Equal("denied", (await Denied(new Tattach(1, 1, NinePConstants.NoFid, "worker", "/"), null)).Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await resolver.ResolveAsync("s",
            new Tattach(1, 1, NinePConstants.NoFid, "worker", "/"), NinePDialect.NineP2000, certificate, new CancellationToken(true)));

        var attach = await resolver.ResolveAsync("s", new Tattach(1, 1, NinePConstants.NoFid, "worker", "/"), NinePDialect.NineP2000,
            certificate, CancellationToken.None);
        Assert.Equal("worker", attach.User);
        Assert.IsType<AuthorizedResourceOperations>(attach.Resources);
        var second = await resolver.ResolveAsync("t", new Tattach(1, 1, NinePConstants.NoFid, "worker", "/"), NinePDialect.NineP2000,
            certificate, CancellationToken.None);
        Assert.Equal(attach.ProcessId + 1, second.ProcessId);
        Assert.NotEqual(attach.ProcessGroupId, second.ProcessGroupId);
    }

    [Fact]
    public async Task EveryDependencyIsRequired()
    {
        using X509Certificate2 certificate = FogNamespaceCluster.Certificate("worker.test");
        (_, Dependencies d) = await Resolver(certificate);
        Assert.Equal("grains", Assert.Throws<ArgumentNullException>(() => new FogNamespaceAttachResolver(null!, d.Root, d.Nodes, d.Authority, d.Resources, d.Ancestry)).ParamName);
        Assert.Equal("root", Assert.Throws<ArgumentNullException>(() => new FogNamespaceAttachResolver(Grains, null!, d.Nodes, d.Authority, d.Resources, d.Ancestry)).ParamName);
        Assert.Equal("nodes", Assert.Throws<ArgumentNullException>(() => new FogNamespaceAttachResolver(Grains, d.Root, null!, d.Authority, d.Resources, d.Ancestry)).ParamName);
        Assert.Equal("authority", Assert.Throws<ArgumentNullException>(() => new FogNamespaceAttachResolver(Grains, d.Root, d.Nodes, null!, d.Resources, d.Ancestry)).ParamName);
        Assert.Equal("resources", Assert.Throws<ArgumentNullException>(() => new FogNamespaceAttachResolver(Grains, d.Root, d.Nodes, d.Authority, null!, d.Ancestry)).ParamName);
        Assert.Equal("ancestry", Assert.Throws<ArgumentNullException>(() => new FogNamespaceAttachResolver(Grains, d.Root, d.Nodes, d.Authority, d.Resources, null!)).ParamName);
        Assert.Equal("resolver", Assert.Throws<ArgumentNullException>(() => new OrleansResourceAncestry(null!)).ParamName);
        Assert.Equal("provider", Assert.Throws<ArgumentNullException>(() => new SharedRootAncestry(null!, new Dictionary<ResourceIdentity, ResourceIdentity>())).ParamName);
        Assert.Equal("mountParents", Assert.Throws<ArgumentNullException>(() => new SharedRootAncestry(d.Ancestry, null!)).ParamName);
    }

    [Fact]
    public async Task OnlyAncestryProvidersReportParentsAndCancellationIsHonoured()
    {
        var registered = new RegisteredMountableResourceResolver(Grains,
        [
            ResourceProviderRegistration.For<ITestApplicationGrain>(TestApplicationGrain.Provider),
            ResourceProviderRegistration.For<IPlainResourceGrain>("plain"),
        ]);
        var ancestry = new OrleansResourceAncestry(registered);
        string device = Guid.NewGuid().ToString("N");
        await Grains.GetGrain<ITestApplicationGrain>(device).SeedAsync("file", "x");
        var file = new ResourceHandle(new ResourceIdentity(TestApplicationGrain.Provider, device, 2), QidType.QTFILE);
        Assert.Equal(new ResourceIdentity(TestApplicationGrain.Provider, device, 1), (await ancestry.GetParentAsync(file, CancellationToken.None))!.Identity);
        Assert.Null(await ancestry.GetParentAsync(new ResourceHandle(new ResourceIdentity("plain", device, 2), QidType.QTFILE), CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ancestry.GetParentAsync(file, new CancellationToken(true)));
    }

    [Fact]
    public async Task AProvidersOwnParentWinsOverTheSharedRootsMountMap()
    {
        var provider = new FixedAncestry { [Child.Identity] = Parent };
        var mountPoint = new ResourceIdentity("root", "r", 9);
        var mapped = new SharedRootAncestry(provider, new Dictionary<ResourceIdentity, ResourceIdentity>
        {
            [Child.Identity] = mountPoint,
            [Parent.Identity] = mountPoint,
        });
        Assert.Equal(Parent, await mapped.GetParentAsync(Child, CancellationToken.None));
        Assert.Equal(new ResourceHandle(mountPoint, QidType.QTDIR), await mapped.GetParentAsync(Parent, CancellationToken.None));
        Assert.Null(await mapped.GetParentAsync(new ResourceHandle(mountPoint, QidType.QTDIR), CancellationToken.None));
    }

    private static async Task<(FogNamespaceAttachResolver Resolver, Dependencies Dependencies)> Resolver(X509Certificate2 certificate)
    {
        FogSharedRoot root = await FogSharedRoot.CreateAsync(Grains, $"resolver-{Guid.NewGuid():N}");
        var nodes = new FogNodePolicy(1, [new FogNodeEnrollment("worker", new string('1', 64), FogNodePolicy.SpkiPin(certificate), "worker.test")]);
        var authority = new FogAuthorizationAuthority(new AuthorizationPolicy(1, [new AuthorizationPrincipal("worker")], [], []));
        var registered = new RegisteredMountableResourceResolver(Grains, [ResourceProviderRegistration.For<IFogRootGrain>(FogSharedRoot.Provider)]);
        var dependencies = new Dependencies(root, nodes, authority, new OrleansResourceOperations(registered), new OrleansResourceAncestry(registered));
        return (new FogNamespaceAttachResolver(Grains, root, nodes, authority, dependencies.Resources, dependencies.Ancestry), dependencies);
    }

    private sealed record Dependencies(FogSharedRoot Root, FogNodePolicy Nodes, FogAuthorizationAuthority Authority,
        IResourceDataOperations Resources, IResourceAncestry Ancestry);

    private sealed class FixedAncestry : Dictionary<ResourceIdentity, ResourceHandle>, IResourceAncestry
    {
        public ValueTask<ResourceHandle?> GetParentAsync(ResourceHandle resource, CancellationToken cancellationToken)
            => ValueTask.FromResult(TryGetValue(resource.Identity, out ResourceHandle? parent) ? parent : null);
    }
}
