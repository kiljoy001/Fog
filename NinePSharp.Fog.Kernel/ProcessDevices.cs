using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

internal sealed class ProcessDevices(Process caller, IResourceDataOperations files) : IResourceDataOperations
{
    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
        => Device(directory).WalkAsync(directory, name, cancellationToken);

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => Device(directory).ReadDirectoryAsync(directory, cancellationToken);

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => Device(directory).CreateAsync(directory, name, directoryEntry, cancellationToken);

    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => Device(resource).OpenAsync(resource, mode, context, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => Device(openHandle.Resource).ReadAsync(openHandle, offset, count, cancellationToken);

    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        => Device(openHandle.Resource).WriteAsync(openHandle, offset, data, context, cancellationToken);

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
        => Device(resource).StatAsync(resource, cancellationToken);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => Device(directory).CreateAndOpenAsync(directory, name, permissions, mode, context, cancellationToken);

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => Device(openHandle.Resource).ClunkAsync(openHandle, context, cancellationToken);

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => Device(resource).RemoveAsync(resource, openHandle, context, cancellationToken);

    private IResourceDataOperations Device(ResourceHandle handle)
        => handle.Identity.Provider == caller.Environment.Provider ? caller.Environment : files;
}
