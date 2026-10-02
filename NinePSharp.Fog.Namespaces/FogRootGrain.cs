using NinePSharp.Constants;
using NinePSharp.Namespaces.Orleans;
using Orleans.Runtime;

namespace NinePSharp.Fog.Namespaces;

/// <summary>Serves a shared root keyed by its device name.</summary>
public sealed class FogRootGrain : Grain, IFogRootGrain
{
    internal const string Owner = "fog";
    private const ulong RootPath = 1;
    private readonly IPersistentState<FogRootState> state;

    public FogRootGrain([PersistentState("fog-root")] IPersistentState<FogRootState> state) => this.state = state;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        if (state.State.Entries.ContainsKey(RootPath))
        {
            return;
        }

        state.State.LastPath = RootPath;
        state.State.Entries[RootPath] = new FogRootEntry { Name = "/", Directory = true };
        foreach (string name in new[] { "mnt", "bin", "n" })
        {
            Add(RootPath, name, directory: true);
        }

        await state.WriteStateAsync();
    }

    /// <inheritdoc/>
    public async Task<ResourceHandleModel> CreateEntryAsync(string path, bool directory)
    {
        string[] names = Names(path);
        if (names.Length == 0)
        {
            throw new ArgumentException("The root already exists.", nameof(path));
        }

        ulong parent = Find(names[..^1]) ?? throw new DirectoryNotFoundException($"The parent of '{path}' does not exist.");
        if (!state.State.Entries[parent].Directory)
        {
            throw new DirectoryNotFoundException($"The parent of '{path}' is not a directory.");
        }

        if (state.State.Entries[parent].Children.TryGetValue(names[^1], out ulong existing))
        {
            if (state.State.Entries[existing].Directory != directory)
            {
                throw new IOException($"'{path}' exists with a different kind.");
            }

            return Handle(existing);
        }

        ulong created = Add(parent, names[^1], directory);
        await state.WriteStateAsync();
        return Handle(created);
    }

    /// <inheritdoc/>
    public Task<ResourceHandleModel?> LookupAsync(string path)
        => Task.FromResult(Find(Names(path)) is ulong found ? Handle(found) : null);

    /// <inheritdoc/>
    public Task<ResourceHandleModel?> GetParentAsync(ResourceHandleModel resource)
        => Task.FromResult(Entry(resource).Parent == 0 ? null : Handle(Entry(resource).Parent));

    /// <inheritdoc/>
    public Task<ResourceHandleModel?> WalkAsync(ResourceHandleModel directory, string name)
        => Task.FromResult(Entry(directory).Children.TryGetValue(name, out ulong child) ? Handle(child) : null);

    /// <inheritdoc/>
    public Task<ResourceDirectoryEntryModel[]> ReadDirectoryAsync(ResourceHandleModel directory)
        => Task.FromResult(Entry(directory).Children.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ResourceDirectoryEntryModel(pair.Key, Handle(pair.Value)))
            .ToArray());

    /// <inheritdoc/>
    public Task<ResourceHandleModel> CreateAsync(ResourceHandleModel directory, string name, bool directoryEntry)
        => throw new ResourceCreateRejectedGrainException("permission denied");

    /// <inheritdoc/>
    public Task<ResourceOpenHandleModel> OpenAsync(ResourceHandleModel resource, byte mode, ResourceOperationContextModel context)
    {
        _ = Entry(resource);
        if ((mode & 3) is NinePConstants.OWRITE or NinePConstants.ORDWR || (mode & (NinePConstants.OTRUNC | NinePConstants.ORCLOSE)) != 0)
        {
            throw new UnauthorizedAccessException("permission denied");
        }

        return Task.FromResult(new ResourceOpenHandleModel(resource, $"{context.OperationId.SessionId}:{context.OperationId.Sequence}", mode, 0));
    }

    /// <inheritdoc/>
    public Task<byte[]> ReadAsync(ResourceOpenHandleModel openHandle, ulong offset, uint count) => Task.FromResult(Array.Empty<byte>());

    /// <inheritdoc/>
    public Task<uint> WriteAsync(ResourceOpenHandleModel openHandle, ulong offset, byte[] data, ResourceOperationContextModel context)
        => throw new UnauthorizedAccessException("permission denied");

    /// <inheritdoc/>
    public Task<ResourceStatModel> StatAsync(ResourceHandleModel resource)
    {
        FogRootEntry entry = Entry(resource);
        uint mode = entry.Directory ? (uint)NinePConstants.FileMode9P.DMDIR | 0x16D : 0x124;
        return Task.FromResult(new ResourceStatModel(resource, entry.Name, mode, 0, 0, 0, Owner, Owner, Owner));
    }

    /// <inheritdoc/>
    public Task<ResourceOpenHandleModel> CreateAndOpenAsync(
        ResourceHandleModel directory,
        string name,
        uint permissions,
        byte mode,
        ResourceOperationContextModel context)
        => throw new ResourceCreateRejectedGrainException("permission denied");

    /// <inheritdoc/>
    public Task ClunkAsync(ResourceOpenHandleModel openHandle, ResourceOperationContextModel context) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task RemoveAsync(ResourceHandleModel resource, ResourceOpenHandleModel? openHandle, ResourceOperationContextModel context)
        => throw new UnauthorizedAccessException("permission denied");

    private static string[] Names(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] != '/')
        {
            throw new ArgumentException("Shared root paths are absolute.", nameof(path));
        }

        string[] names = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (names.Any(name => name is "." or ".."))
        {
            throw new ArgumentException("Shared root paths are normalized.", nameof(path));
        }

        return names;
    }

    private ulong? Find(IEnumerable<string> names)
    {
        ulong current = RootPath;
        foreach (string name in names)
        {
            if (!state.State.Entries[current].Children.TryGetValue(name, out current))
            {
                return null;
            }
        }

        return current;
    }

    private ulong Add(ulong parent, string name, bool directory)
    {
        ulong path = ++state.State.LastPath;
        state.State.Entries[path] = new FogRootEntry { Name = name, Parent = parent, Directory = directory };
        state.State.Entries[parent].Children[name] = path;
        return path;
    }

    private FogRootEntry Entry(ResourceHandleModel resource)
        => state.State.Entries.TryGetValue(resource.Identity.Path, out FogRootEntry? entry)
            ? entry
            : throw new FileNotFoundException("file does not exist");

    private ResourceHandleModel Handle(ulong path)
        => new(
            new ResourceIdentityModel(FogSharedRoot.Provider, this.GetPrimaryKeyString(), path),
            state.State.Entries[path].Directory ? QidType.QTDIR : QidType.QTFILE,
            0);
}
