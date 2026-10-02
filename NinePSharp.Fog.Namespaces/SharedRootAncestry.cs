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
/// A provider's parent relation, extended across the shared root's operator mounts: a provider
/// root mounted in the shared root has the mount point as its parent.
/// </summary>
public sealed class SharedRootAncestry : IResourceAncestry
{
    private readonly IResourceAncestry provider;
    private readonly IReadOnlyDictionary<ResourceIdentity, ResourceIdentity> mountParents;

    public SharedRootAncestry(IResourceAncestry provider, IReadOnlyDictionary<ResourceIdentity, ResourceIdentity> mountParents)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.mountParents = mountParents ?? throw new ArgumentNullException(nameof(mountParents));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceHandle?> GetParentAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        ResourceHandle? parent = await provider.GetParentAsync(resource, cancellationToken);
        if (parent is not null)
        {
            return parent;
        }

        return mountParents.TryGetValue(resource.Identity, out ResourceIdentity? mountPoint)
            ? new ResourceHandle(mountPoint, QidType.QTDIR)
            : null;
    }
}
