using System.Text;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

/// <summary>Stat records for the kernel's devices, and wstat updates handed to the device that owns the file.</summary>
internal sealed class KernelFileStats(ProcessDevices devices) : IFileStatOperations
{
    public async ValueTask<ReadOnlyMemory<byte>> StatAsync(ResourceHandle resource, uint count, CancellationToken cancellationToken)
        => Bound(Encode(ToStat(await devices.StatAsync(resource, cancellationToken))), count);

    public async ValueTask<ReadOnlyMemory<byte>> StatAsync(ResourceOpenHandle handle, uint count, CancellationToken cancellationToken)
        => Bound(Encode(ToStat(await devices.StatOpenAsync(handle, cancellationToken))), count);

    public ValueTask<uint> WStatAsync(ResourceHandle resource, ReadOnlyMemory<byte> stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => devices.WStatAsync(resource, Decode(stat), context, cancellationToken);

    public ValueTask<uint> WStatAsync(ResourceOpenHandle handle, ReadOnlyMemory<byte> stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => devices.WStatOpenAsync(handle, Decode(stat), context, cancellationToken);

    internal static Stat ToStat(ResourceStat stat)
    {
        int size = 49 + new[] { stat.Name, stat.User, stat.Group, stat.LastModifier }.Sum(Encoding.UTF8.GetByteCount);
        return new Stat((ushort)size, 0, 0, stat.Resource.Qid, stat.Mode, stat.AccessTime, stat.ModificationTime, stat.Length, stat.Name, stat.User, stat.Group, stat.LastModifier);
    }

    internal static byte[] Encode(Stat stat)
    {
        byte[] record = new byte[stat.Size];
        int offset = 0;
        stat.WriteTo(record, ref offset);
        return record;
    }

    // A buffer too small for the record gets its two-byte size, as stat(5) says.
    internal static ReadOnlyMemory<byte> Bound(byte[] record, uint count) => record.Length <= count ? record : record.AsMemory(0, 2);

    private static ResourceWStat Decode(ReadOnlyMemory<byte> record)
    {
        int offset = 0;
        var stat = new Stat(record.Span, ref offset);
        return new ResourceWStat(stat.Type, stat.Dev, stat.Qid, stat.Mode, stat.Atime, stat.Mtime, stat.Length, stat.Name, stat.Uid, stat.Gid, stat.Muid, (uint)record.Length);
    }
}
