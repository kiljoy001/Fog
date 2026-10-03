using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel.Tests.Support;

internal sealed class OpenCounter : IResourceDataOperations
{
    private readonly RamFs files = new("ram", "#R", "none");
    private int open;

    public ResourceHandle Root => files.Root;

    public int Open => Volatile.Read(ref open);

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
        => files.WalkAsync(directory, name, cancellationToken);

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => files.ReadDirectoryAsync(directory, cancellationToken);

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => files.CreateAsync(directory, name, directoryEntry, cancellationToken);

    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref open);
        return files.OpenAsync(resource, mode, context, cancellationToken);
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => files.ReadAsync(openHandle, offset, count, cancellationToken);

    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        => files.WriteAsync(openHandle, offset, data, context, cancellationToken);

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
        => files.StatAsync(resource, cancellationToken);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref open);
        return files.CreateAndOpenAsync(directory, name, permissions, mode, context, cancellationToken);
    }

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        Interlocked.Decrement(ref open);
        return files.ClunkAsync(openHandle, context, cancellationToken);
    }

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => files.RemoveAsync(resource, openHandle, context, cancellationToken);
}
