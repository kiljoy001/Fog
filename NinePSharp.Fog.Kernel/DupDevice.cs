using System.Globalization;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devdup: descriptor N is file N at path 2N+1, and its ctl file Nctl is at 2N+2.
internal sealed class DupDevice(VProcess process, string owner) : IResourceDataOperations
{
    public const string Provider = "dup";
    private static readonly uint[] Permissions = [0b100_000_000, 0b010_000_000, 0b110_000_000];

    public static ResourceHandle Root { get; } = new(new ResourceIdentity(Provider, "#d", 0), QidType.QTDIR);

    // sysfile.c:openmode, as a channel's mode records it.
    public static int OpenMode(int mode) => (mode & 3) == NinePConstants.OEXEC ? NinePConstants.OREAD : mode & 3;

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
        => ValueTask.FromResult(Entries().FirstOrDefault(entry => entry.Name == name)?.Handle);

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ResourceDirectoryEntry>>(Entries().ToArray());

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        if (resource.IsDirectory)
        {
            return ValueTask.FromResult(new ResourceStat(resource, ".", (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101, 0, 0, 0, owner, owner, owner));
        }

        ulong twice = resource.Identity.Path - 1;
        int fd = (int)(twice / 2);
        bool ctl = (twice & 1) != 0;
        uint mode = ctl ? 0b100_000_000 : Permissions[OpenMode(Slot(fd).Handle.Mode)];
        string name = fd.ToString(CultureInfo.InvariantCulture) + (ctl ? "ctl" : string.Empty);
        return ValueTask.FromResult(new ResourceStat(resource, name, mode, 0, 0, 0, owner, owner, owner));
    }

    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if ((mode & NinePConstants.ORCLOSE) != 0)
        {
            throw new IOException(Errors.Permission);
        }

        if (resource.IsDirectory)
        {
            return ValueTask.FromResult(new ResourceOpenHandle(resource, "#d", mode, 0));
        }

        ulong twice = resource.Identity.Path - 1;
        return (twice & 1) == 0
            ? throw new DupOpenException((int)(twice / 2))
            : throw new NotSupportedException("ctl files are not implemented");
    }

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    private static ResourceHandle File(ulong path) => new(new ResourceIdentity(Provider, "#d", path), QidType.QTFILE);

    private IEnumerable<ResourceDirectoryEntry> Entries()
    {
        foreach (DescriptorSlot slot in process.Descriptors.Snapshot())
        {
            string name = slot.Number.ToString(CultureInfo.InvariantCulture);
            ulong twice = 2 * (ulong)slot.Number;
            yield return new ResourceDirectoryEntry(name, File(twice + 1));
            yield return new ResourceDirectoryEntry(name + "ctl", File(twice + 2));
        }
    }

    private DescriptorSlot Slot(int fd) => process.Descriptors.Snapshot().ToDictionary(slot => slot.Number)[fd];
}
