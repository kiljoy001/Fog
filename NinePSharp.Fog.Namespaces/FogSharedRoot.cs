using NinePSharp.Constants;
using NinePSharp.Namespaces;
using NinePSharp.Namespaces.Orleans;

namespace NinePSharp.Fog.Namespaces;

/// <summary>
/// A host's shared namespace: a root directory grain laid out by Plan 9 namespace(4) convention
/// (/mnt for applications, /bin for modules, /n for remote hosts) and the process-group grain
/// whose mounts every attach copies.
/// </summary>
public sealed class FogSharedRoot
{
    /// <summary>The provider name of shared root resources.</summary>
    public const string Provider = "fog-root";

    private readonly IGrainFactory grains;

    private FogSharedRoot(IGrainFactory grains, string id)
    {
        this.grains = grains;
        ProcessGroupId = id;
        Root = new ResourceHandle(new ResourceIdentity(Provider, id, 1), QidType.QTDIR);
    }

    /// <summary>Gets the process group holding the shared mounts.</summary>
    public string ProcessGroupId { get; }

    /// <summary>Gets the root directory every attach starts from.</summary>
    public ResourceHandle Root { get; }

    private IFogRootGrain Directory => grains.GetGrain<IFogRootGrain>(ProcessGroupId);

    /// <summary>Creates, or reopens, the shared root with the given identity.</summary>
    public static async Task<FogSharedRoot> CreateAsync(IGrainFactory grains, string id)
    {
        ArgumentNullException.ThrowIfNull(grains);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var root = new FogSharedRoot(grains, id);
        await root.Directory.InitializeAsync();
        IVProcessGroupGrain group = grains.GetGrain<IVProcessGroupGrain>(id);
        try
        {
            await group.InitializeEmptyAsync();
        }
        catch (InvalidOperationException)
        {
            // Already initialized with mounts: reopening keeps them.
        }

        return root;
    }

    /// <summary>Mounts an application's root directory at /mnt/{name}.</summary>
    public async Task MountApplicationAsync(string name, ResourceHandle applicationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(applicationRoot);
        if (name.Contains('/') || name is "." or "..")
        {
            throw new ArgumentException("An application name is one path element.", nameof(name));
        }

        if (!applicationRoot.IsDirectory)
        {
            throw new ArgumentException("An application's root is a directory.", nameof(applicationRoot));
        }

        ResourceHandle mountPoint = await CreateEntryAsync($"/mnt/{name}", directory: true);
        await grains.GetGrain<IVProcessGroupGrain>(ProcessGroupId)
            .MountAsync(applicationRoot.ToModel(), mountPoint.ToModel(), MountFlags.Replace);
    }

    /// <summary>Adds a directory or file to the shared root.</summary>
    public async Task<ResourceHandle> CreateEntryAsync(string path, bool directory)
        => (await Directory.CreateEntryAsync(path, directory)).ToDomain();

    /// <summary>Returns the shared root entry at an absolute path, such as a mount point to grant.</summary>
    public async Task<ResourceHandle> ResolveAsync(string path)
        => (await Directory.LookupAsync(path))?.ToDomain() ?? throw new FileNotFoundException($"'{path}' is not in the shared root.");

    /// <summary>
    /// Maps each resource mounted in the shared root to the directory it is mounted on. Only these
    /// operator mounts extend containment; a principal's own binds never do.
    /// </summary>
    public async Task<IReadOnlyDictionary<ResourceIdentity, ResourceIdentity>> MountParentsAsync()
    {
        NamespaceSnapshotModel snapshot = await grains.GetGrain<IVProcessGroupGrain>(ProcessGroupId).GetSnapshotAsync();
        var parents = new Dictionary<ResourceIdentity, ResourceIdentity>();
        foreach (MountHeadModel head in snapshot.MountHeads)
        {
            foreach (MountBindingModel mount in head.Mounts)
            {
                parents.TryAdd(mount.Target.Identity.ToDomain(), head.From.Identity.ToDomain());
            }
        }

        return parents;
    }
}
