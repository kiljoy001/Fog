using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using NinePSharp.Namespaces.Authorization;
using NinePSharp.Namespaces.Orleans;
using NinePSharp.Namespaces.Orleans.Server;

namespace NinePSharp.Fog.Namespaces;

/// <summary>Reads the parent relation from <see cref="IAncestryResourceGrain"/> providers; others report none.</summary>
public sealed class OrleansResourceAncestry : IResourceAncestry
{
    private readonly IMountableResourceResolver resolver;

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
