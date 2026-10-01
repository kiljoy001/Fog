using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using NinePSharp.Namespaces.Authorization;
using NinePSharp.Namespaces.Orleans;
using NinePSharp.Namespaces.Orleans.Server;

namespace NinePSharp.Fog.Namespaces;

/// <summary>
/// Resolves an enrolled node's attach: the principal comes only from its certificate, its
/// namespace is a process-group grain copied from the shared root, and its session's resource
/// operations are that principal's authorization view.
/// </summary>
public sealed class FogNamespaceAttachResolver : IDistributedNamespaceAttachResolver
{
    private readonly IGrainFactory grains;
    private readonly FogSharedRoot root;
    private readonly FogNodePolicy nodes;
    private readonly FogAuthorizationAuthority authority;
    private readonly IResourceDataOperations resources;
    private readonly IResourceAncestry ancestry;
    private long nextProcess;

    /// <summary>Initializes the resolver.</summary>
    public FogNamespaceAttachResolver(IGrainFactory grains, FogSharedRoot root, FogNodePolicy nodes,
        FogAuthorizationAuthority authority, IResourceDataOperations resources, IResourceAncestry ancestry)
    {
        this.grains = grains ?? throw new ArgumentNullException(nameof(grains));
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        this.nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
    }

    /// <inheritdoc/>
    public async ValueTask<DistributedNamespaceAttach> ResolveAsync(string sessionId, Tattach request, NinePDialect dialect,
        X509Certificate2? certificate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Afid != NinePConstants.NoFid || request.Aname != "/") throw new FogException("denied");
        FogPrincipal principal = nodes.Attach(request.Uname, certificate);
        string group = $"{root.ProcessGroupId}/attach/{Guid.NewGuid():N}";
        await grains.GetGrain<IVProcessGroupGrain>(root.ProcessGroupId).CloneToAsync(group);
        var view = new AuthorizedResourceOperations(resources, new SharedRootAncestry(ancestry, await root.MountParentsAsync()),
            authority.Current, principal.Node, () => authority.Generation);
        return new DistributedNamespaceAttach(group, Interlocked.Increment(ref nextProcess), principal.Node, root.Root) { Resources = view };
    }
}

/// <summary>Reads the parent relation from <see cref="IAncestryResourceGrain"/> providers; others report none.</summary>
public sealed class OrleansResourceAncestry : IResourceAncestry
{
    private readonly IMountableResourceResolver resolver;

    /// <summary>Initializes the adapter over the registered providers.</summary>
    public OrleansResourceAncestry(IMountableResourceResolver resolver)
        => this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <inheritdoc/>
    public async ValueTask<ResourceHandle?> GetParentAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return resolver.Resolve(resource.Identity.ToModel()) is IAncestryResourceGrain grain
            ? (await grain.GetParentAsync(resource.ToModel()))?.ToDomain()
            : null;
    }
}

/// <summary>
/// A provider's parent relation, extended across the shared root's operator mounts: a provider
/// root mounted in the shared root has the mount point as its parent.
/// </summary>
public sealed class SharedRootAncestry : IResourceAncestry
{
    private readonly IResourceAncestry provider;
    private readonly IReadOnlyDictionary<ResourceIdentity, ResourceIdentity> mountParents;

    /// <summary>Initializes the relation.</summary>
    public SharedRootAncestry(IResourceAncestry provider, IReadOnlyDictionary<ResourceIdentity, ResourceIdentity> mountParents)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.mountParents = mountParents ?? throw new ArgumentNullException(nameof(mountParents));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceHandle?> GetParentAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        ResourceHandle? parent = await provider.GetParentAsync(resource, cancellationToken);
        if (parent is not null) return parent;
        return mountParents.TryGetValue(resource.Identity, out ResourceIdentity? mountPoint)
            ? new ResourceHandle(mountPoint, QidType.QTDIR)
            : null;
    }
}
