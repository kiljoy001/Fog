using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devcons's null, pid, ppid, user and zero, in consdir's order; each reads as the calling process sees it.
internal sealed class ConsDevice(Process caller, string owner) : IResourceDataOperations
{
    public const string Provider = "cons";
    private const int NumSize = 12;
    private static readonly (string Name, uint Permissions)[] Files =
    [
        ("null", 0b110_110_110),
        ("pid", 0b100_100_100),
        ("ppid", 0b100_100_100),
        ("user", 0b110_110_110),
        ("zero", 0b100_100_100),
    ];

    private static readonly uint[] Access = [0b100_000_000, 0b010_000_000, 0b110_000_000, 0b001_000_000];

    public static ResourceHandle Root { get; } = new(new ResourceIdentity(Provider, "#c", 0), QidType.QTDIR);

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        int index = Array.FindIndex(Files, file => file.Name == name);
        return ValueTask.FromResult(index < 0 ? null : File(index));
    }

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ResourceDirectoryEntry>>(Files.Select((file, index) => new ResourceDirectoryEntry(file.Name, File(index))).ToArray());

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        (string name, uint permissions) = resource.IsDirectory
            ? ("#c", (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101)
            : Files[resource.Identity.Path - 1];
        return ValueTask.FromResult(new ResourceStat(resource, name, permissions, 0, 0, 0, owner, owner, owner));
    }

    // devpermcheck; the files give owner, group and others the same bits, so the owner's serve for all.
    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        uint access = Access[mode & 3];
        if (!resource.IsDirectory && (Files[resource.Identity.Path - 1].Permissions & access) != access)
        {
            throw new IOException(Errors.Permission);
        }

        string handle = $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";
        return ValueTask.FromResult(new ResourceOpenHandle(resource, handle, mode, 0));
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> data = Files[openHandle.Resource.Identity.Path - 1].Name switch
        {
            "null" => ReadOnlyMemory<byte>.Empty,
            "pid" => Slice(Number(caller.Pid), offset, count),
            "ppid" => Slice(Number(caller.ParentPid), offset, count),
            "user" => Slice(Encoding.UTF8.GetBytes(caller.User), offset, count),
            _ => new byte[count],
        };
        return ValueTask.FromResult(data);
    }

    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        // userwrite: a process may only give up its user, becoming none.
        if (Files[openHandle.Resource.Identity.Path - 1].Name == "user")
        {
            caller.User = data.Span.SequenceEqual("none"u8) ? "none" : throw new IOException(Errors.Permission);
        }

        return ValueTask.FromResult((uint)data.Length);
    }

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    private static ResourceHandle File(int index) => new(new ResourceIdentity(Provider, "#c", (ulong)index + 1), QidType.QTFILE);

    // readnum: right-aligned in NUMSIZE-1 places and a space.
    private static byte[] Number(long value) => Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture).PadLeft(NumSize - 1) + " ");

    private static ReadOnlyMemory<byte> Slice(byte[] data, ulong offset, uint count)
    {
        int start = (int)Math.Min(offset, (ulong)data.Length);
        return data.AsMemory(start, (int)Math.Min(count, (uint)(data.Length - start)));
    }
}
