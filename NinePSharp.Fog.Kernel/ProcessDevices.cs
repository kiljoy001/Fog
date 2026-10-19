using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

internal sealed class ProcessDevices(Process caller, IResourceDataOperations files, PipeDevice pipes, RootDevice slash, MountDevice mounts, ProcDevice procs, IpDevice ip, SrvDevice srv)
    : IResourceDataOperations, IResourceOpenStatOperations, IResourceWStatOperations
{
    public async ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
        => await Device(directory).WalkAsync(directory, name, cancellationToken);

    public async ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => await Device(directory).ReadDirectoryAsync(directory, cancellationToken);

    public async ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => await Device(directory).CreateAsync(directory, name, directoryEntry, cancellationToken);

    public async ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Device(resource).OpenAsync(resource, mode, context, cancellationToken);

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => await Device(openHandle.Resource).ReadAsync(openHandle, offset, count, cancellationToken);

    public async ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Device(openHandle.Resource).WriteAsync(openHandle, offset, data, context, cancellationToken);

    public async ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
        => await Device(resource).StatAsync(resource, cancellationToken);

    public async ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Device(directory).CreateAndOpenAsync(directory, name, permissions, mode, context, cancellationToken);

    public async ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Device(openHandle.Resource).ClunkAsync(openHandle, context, cancellationToken);

    public async ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Device(resource).RemoveAsync(resource, openHandle, context, cancellationToken);

    // A device without its own open-file stat reports the file the descriptor names.
    public async ValueTask<ResourceStat> StatOpenAsync(ResourceOpenHandle handle, CancellationToken cancellationToken)
        => Device(handle.Resource) is IResourceOpenStatOperations opened
            ? await opened.StatOpenAsync(handle, cancellationToken)
            : await Device(handle.Resource).StatAsync(handle.Resource, cancellationToken);

    public async ValueTask<uint> WStatAsync(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Updates(resource).WStatAsync(resource, stat, context, cancellationToken);

    public async ValueTask<uint> WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Updates(handle.Resource).WStatOpenAsync(handle, stat, context, cancellationToken);

    internal IResourceDataOperations Device(ResourceHandle handle) => handle.Identity.Provider switch
    {
        PipeDevice.Provider => pipes,
        RootDevice.Provider => slash,
        MountDevice.Provider => mounts,
        ProcDevice.Provider => procs,
        IpDevice.Provider => ip,
        SrvDevice.Provider => srv,
        DupDevice.Provider => caller.Dup,
        ConsDevice.Provider => caller.Cons,
        _ when handle.Identity.Provider == caller.Environment.Provider => caller.Environment,
        _ => files,
    };

    // devwstat: a device that does not change entries refuses with Eperm.
    private IResourceWStatOperations Updates(ResourceHandle resource)
        => Device(resource) as IResourceWStatOperations ?? throw new ResourceWStatRejectedException(Errors.Permission);
}
