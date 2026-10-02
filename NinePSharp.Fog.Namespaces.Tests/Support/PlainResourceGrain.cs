using System.Collections.Concurrent;
using NinePSharp.Constants;
using NinePSharp.Namespaces.Orleans;

namespace NinePSharp.Fog.Namespaces.Tests.Support;

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

    public Task<ResourceOpenHandleModel> CreateAndOpenAsync(
        ResourceHandleModel directory,
        string name,
        uint permissions,
        byte mode,
        ResourceOperationContextModel context)
        => throw new NotSupportedException();

    public Task ClunkAsync(ResourceOpenHandleModel openHandle, ResourceOperationContextModel context) => throw new NotSupportedException();

    public Task RemoveAsync(ResourceHandleModel resource, ResourceOpenHandleModel? openHandle, ResourceOperationContextModel context)
        => throw new NotSupportedException();
}
