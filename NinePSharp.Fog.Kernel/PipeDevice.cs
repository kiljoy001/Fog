using System.Globalization;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devpipe: each pipe is a pair of ends; an end reads its own queue and writes the other's.
internal sealed class PipeDevice(string owner) : IResourceDataOperations, IResourceWStatOperations
{
    public const string Provider = "pipe";
    private readonly object gate = new();
    private readonly Dictionary<string, Pipe> pipes = new();
    private long next;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return pipes.Count;
            }
        }
    }

    public (ResourceHandle First, ResourceHandle Second) Create()
    {
        string device = Interlocked.Increment(ref next).ToString(CultureInfo.InvariantCulture);
        lock (gate)
        {
            pipes.Add(device, new Pipe());
        }

        return (End(device, 0), End(device, 1));
    }

    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            pipes[resource.Identity.Device].Opens[resource.Identity.Path]++;
        }

        string handle = $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";
        return ValueTask.FromResult(new ResourceOpenHandle(resource, handle, mode, 0));
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => Queue(openHandle.Resource, 0).ReadAsync((int)count, cancellationToken);

    public async ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        => (uint)await Queue(openHandle.Resource, 1).WriteAsync(data, cancellationToken);

    // Closing the last descriptor for an end hangs up the other end's reads and fails its writes.
    // What was queued toward the closed end goes with the pipe.
    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        ResourceIdentity end = openHandle.Resource.Identity;
        lock (gate)
        {
            Pipe pipe = pipes[end.Device];
            if (--pipe.Opens[end.Path] == 0)
            {
                pipe.Queues[0].Hangup();
                pipe.Queues[1].Hangup();
            }

            if (pipe.Opens[0] + pipe.Opens[1] == 0)
            {
                pipes.Remove(end.Device);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    // pipestat: an end is data or data1, as long as what is queued for it to read.
    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        ulong end = resource.Identity.Path;
        long queued = Queue(resource, 0).Length;
        return ValueTask.FromResult(new ResourceStat(resource, end == 0 ? "data" : "data1", 0b110_110_110, 0, 0, (ulong)queued, owner, owner, owner));
    }

    public ValueTask<uint> WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => WStatAsync(handle.Resource, stat, context, cancellationToken);

    // pipewstat: the length, unconditionally, becomes both queues' limit.
    public ValueTask<uint> WStatAsync(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (stat.Length > PipeQueue.MaxLimit)
        {
            throw new ResourceWStatRejectedException(Errors.BadArg);
        }

        Queue(resource, 0).SetLimit((int)stat.Length);
        Queue(resource, 1).SetLimit((int)stat.Length);
        return ValueTask.FromResult(stat.EncodedLength);
    }

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    private static ResourceHandle End(string device, ulong end) => new(new ResourceIdentity(Provider, device, end), QidType.QTFILE);

    // An end reads queue[end] and writes queue[1 - end].
    private PipeQueue Queue(ResourceHandle end, ulong writing)
    {
        lock (gate)
        {
            return pipes[end.Identity.Device].Queues[end.Identity.Path ^ writing];
        }
    }

    private sealed class Pipe
    {
        public PipeQueue[] Queues { get; } = [new(), new()];

        public int[] Opens { get; } = [0, 0];
    }
}
