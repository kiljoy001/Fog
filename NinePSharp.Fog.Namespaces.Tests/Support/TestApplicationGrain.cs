using System.Collections.Concurrent;
using NinePSharp.Constants;
using NinePSharp.Namespaces.Orleans;

namespace NinePSharp.Fog.Namespaces.Tests.Support;

/// <summary>An application grain for tests: one root directory of seeded files, owned by "app".</summary>
public interface ITestApplicationGrain : IAncestryResourceGrain
{
    Task SeedAsync(string name, string contents);

    Task<int> GetWritesAsync();
}

public sealed class TestApplicationGrain : Grain, ITestApplicationGrain
{
    internal const string Provider = "test-app";
    private const ulong RootPath = 1;
    private readonly ConcurrentDictionary<ulong, (string Name, byte[] Data)> files = new();
    private ulong nextPath = RootPath;
    private int writes;
    private int nextHandle;

    public Task SeedAsync(string name, string contents)
    {
        files[++nextPath] = (name, System.Text.Encoding.ASCII.GetBytes(contents));
        return Task.CompletedTask;
    }

    public Task<int> GetWritesAsync() => Task.FromResult(writes);

    public Task<ResourceHandleModel?> GetParentAsync(ResourceHandleModel resource)
        => Task.FromResult(resource.Identity.Path == RootPath ? null : Root());

    public Task<ResourceHandleModel?> WalkAsync(ResourceHandleModel directory, string name)
    {
        KeyValuePair<ulong, (string Name, byte[] Data)> match = files.FirstOrDefault(pair => pair.Value.Name == name);
        return Task.FromResult(directory.Identity.Path == RootPath && match.Value.Name is not null ? File(match.Key) : null);
    }

    public Task<ResourceDirectoryEntryModel[]> ReadDirectoryAsync(ResourceHandleModel directory)
        => Task.FromResult(files.OrderBy(pair => pair.Value.Name, StringComparer.Ordinal)
            .Select(pair => new ResourceDirectoryEntryModel(pair.Value.Name, File(pair.Key)))
            .ToArray());

    public Task<ResourceHandleModel> CreateAsync(ResourceHandleModel directory, string name, bool directoryEntry)
        => throw new NotSupportedException("The test application has a fixed file set.");

    public Task<ResourceOpenHandleModel> OpenAsync(ResourceHandleModel resource, byte mode, ResourceOperationContextModel context)
    {
        if ((mode & NinePConstants.OTRUNC) != 0) files[resource.Identity.Path] = (files[resource.Identity.Path].Name, Array.Empty<byte>());
        return Task.FromResult(new ResourceOpenHandleModel(resource, $"open-{Interlocked.Increment(ref nextHandle)}", mode, 8192));
    }

    public Task<byte[]> ReadAsync(ResourceOpenHandleModel openHandle, ulong offset, uint count)
    {
        if (openHandle.Resource.Identity.Path == RootPath) return Task.FromResult(Array.Empty<byte>());
        byte[] data = files[openHandle.Resource.Identity.Path].Data;
        int start = (int)Math.Min(offset, (ulong)data.Length);
        return Task.FromResult(data.AsSpan(start, (int)Math.Min(count, (uint)(data.Length - start))).ToArray());
    }

    public Task<uint> WriteAsync(ResourceOpenHandleModel openHandle, ulong offset, byte[] data, ResourceOperationContextModel context)
    {
        (string name, byte[] existing) = files[openHandle.Resource.Identity.Path];
        byte[] updated = existing.Length >= (int)offset + data.Length ? existing.ToArray() : new byte[(int)offset + data.Length];
        existing.AsSpan(0, Math.Min(existing.Length, updated.Length)).CopyTo(updated);
        data.CopyTo(updated, (int)offset);
        files[openHandle.Resource.Identity.Path] = (name, updated);
        Interlocked.Increment(ref writes);
        return Task.FromResult((uint)data.Length);
    }

    public Task<ResourceStatModel> StatAsync(ResourceHandleModel resource)
    {
        bool root = resource.Identity.Path == RootPath;
        uint mode = root ? (uint)NinePConstants.FileMode9P.DMDIR | 0x1FF : 0x1B6;
        string name = root ? "/" : files[resource.Identity.Path].Name;
        ulong length = root ? 0 : (ulong)files[resource.Identity.Path].Data.Length;
        return Task.FromResult(new ResourceStatModel(resource, name, mode, 0, 0, length, "app", "app", "app"));
    }

    public Task<ResourceOpenHandleModel> CreateAndOpenAsync(ResourceHandleModel directory, string name, uint permissions, byte mode,
        ResourceOperationContextModel context)
        => throw new NotSupportedException("The test application has a fixed file set.");

    public Task ClunkAsync(ResourceOpenHandleModel openHandle, ResourceOperationContextModel context) => Task.CompletedTask;

    public Task RemoveAsync(ResourceHandleModel resource, ResourceOpenHandleModel? openHandle, ResourceOperationContextModel context)
        => throw new NotSupportedException("The test application has a fixed file set.");

    private ResourceHandleModel Root() => new(new ResourceIdentityModel(Provider, this.GetPrimaryKeyString(), RootPath), QidType.QTDIR, 0);

    private ResourceHandleModel File(ulong path) => new(new ResourceIdentityModel(Provider, this.GetPrimaryKeyString(), path), QidType.QTFILE, 0);
}

/// <summary>A provider without the parent capability; it is never called by the tests that use it.</summary>
public interface IPlainResourceGrain : IMountableResourceGrain
{
}

public sealed class PlainResourceGrain : Grain, IPlainResourceGrain
{
    public Task<ResourceHandleModel?> WalkAsync(ResourceHandleModel directory, string name) => throw new NotSupportedException();

    public Task<ResourceDirectoryEntryModel[]> ReadDirectoryAsync(ResourceHandleModel directory) => throw new NotSupportedException();

    public Task<ResourceHandleModel> CreateAsync(ResourceHandleModel directory, string name, bool directoryEntry) => throw new NotSupportedException();

    public Task<ResourceOpenHandleModel> OpenAsync(ResourceHandleModel resource, byte mode, ResourceOperationContextModel context)
        => throw new NotSupportedException();

    public Task<byte[]> ReadAsync(ResourceOpenHandleModel openHandle, ulong offset, uint count) => throw new NotSupportedException();

    public Task<uint> WriteAsync(ResourceOpenHandleModel openHandle, ulong offset, byte[] data, ResourceOperationContextModel context)
        => throw new NotSupportedException();

    public Task<ResourceStatModel> StatAsync(ResourceHandleModel resource) => throw new NotSupportedException();

    public Task<ResourceOpenHandleModel> CreateAndOpenAsync(ResourceHandleModel directory, string name, uint permissions, byte mode,
        ResourceOperationContextModel context)
        => throw new NotSupportedException();

    public Task ClunkAsync(ResourceOpenHandleModel openHandle, ResourceOperationContextModel context) => throw new NotSupportedException();

    public Task RemoveAsync(ResourceHandleModel resource, ResourceOpenHandleModel? openHandle, ResourceOperationContextModel context)
        => throw new NotSupportedException();
}
