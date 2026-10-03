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
/// Resolves an attach to a process-group grain copied from the shared root, whose resource
/// operations are the principal's authorization view. Without an afid the principal is the enrolled
/// node its certificate names; with one, it is the user the afid layer authenticated on that afid.
/// </summary>
public sealed class FogNamespaceAttachResolver : IDistributedNamespaceAttachResolver
{
    private readonly IGrainFactory grains;
    private readonly FogSharedRoot root;
    private readonly FogNodePolicy nodes;
    private readonly FogAuthorizationAuthority authority;
    private readonly IResourceDataOperations resources;
    private readonly IResourceAncestry ancestry;
    private readonly Func<string, uint, string?>? authenticatedUser;
    private long nextProcess;

    public FogNamespaceAttachResolver(
        IGrainFactory grains,
        FogSharedRoot root,
        FogNodePolicy nodes,
        FogAuthorizationAuthority authority,
        IResourceDataOperations resources,
        IResourceAncestry ancestry,
        Func<string, uint, string?>? authenticatedUser = null)
    {
        this.grains = grains ?? throw new ArgumentNullException(nameof(grains));
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        this.nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
        this.authenticatedUser = authenticatedUser;
    }

    /// <inheritdoc/>
    public async ValueTask<DistributedNamespaceAttach> ResolveAsync(
        string sessionId,
        Tattach request,
        NinePDialect dialect,
        X509Certificate2? certificate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 9front's srv and mount attach with an empty name.
        if (request.Aname is not ("/" or ""))
        {
            throw new FogException("denied");
        }

        string principal = request.Afid == NinePConstants.NoFid
            ? nodes.Attach(request.Uname, certificate).Node
            : authenticatedUser?.Invoke(sessionId, request.Afid) ?? throw new FogException("denied");
        string group = $"{root.ProcessGroupId}/attach/{Guid.NewGuid():N}";
        await grains.GetGrain<IVProcessGroupGrain>(root.ProcessGroupId).CloneToAsync(group);
        var view = new AuthorizedResourceOperations(
            resources,
            new SharedRootAncestry(ancestry, await root.MountParentsAsync()),
            authority.Current,
            principal,
            () => authority.Generation);
        return new DistributedNamespaceAttach(group, Interlocked.Increment(ref nextProcess), principal, root.Root) { Resources = view };
    }
}
