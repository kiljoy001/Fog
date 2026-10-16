using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devroot: #/, the mount points the namespace is built on. Its #/boot, which holds 9front's boot
// programs, is left out.
internal sealed class RootDevice(string owner) : IResourceDataOperations
{
    public const string Provider = "root";
    private const uint Mode = (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101;

    private static readonly string[] MountPoints = ["bin", "dev", "env", "fd", "mnt", "n", "net", "net.alt", "proc", "rc", "root", "srv", "shr"];

    public static ResourceHandle Root { get; } = Directory(0);

    public static ResourceHandle MountPoint(string name) => Directory((ulong)Array.IndexOf(MountPoints, name) + 1);

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        int index = Array.IndexOf(MountPoints, name);
        return ValueTask.FromResult(directory.Identity.Path == 0 && index >= 0 ? Directory((ulong)index + 1) : null);
    }

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ResourceDirectoryEntry>>(directory.Identity.Path == 0
            ? MountPoints.Select((name, index) => new ResourceDirectoryEntry(name, Directory((ulong)index + 1))).ToArray()
            : []);

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        string name = resource.Identity.Path == 0 ? "#/" : MountPoints[resource.Identity.Path - 1];
        return ValueTask.FromResult(new ResourceStat(resource, name, Mode, 0, 0, 0, owner, owner, owner));
    }

    // devopen: a directory opens for reading only.
    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if ((mode & 3) != NinePConstants.OREAD)
        {
            throw new IOException(Errors.Permission);
        }

        string handle = $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";
        return ValueTask.FromResult(new ResourceOpenHandle(resource, handle, mode, 0));
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.IsDirectory);

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    private static ResourceHandle Directory(ulong path) => new(new ResourceIdentity(Provider, "#/", path), QidType.QTDIR);
}
