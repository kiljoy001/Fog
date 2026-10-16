using System.Buffers.Binary;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel.Tests.Support;

// The far end of a mount connection, as a device: it answers Tversion as it is told, keeps every
// other request for the test to answer, and gives the connection whatever bytes the test sends.
internal sealed class ScriptedPeer : IResourceDataOperations, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly List<byte[]> requests = [];
    private byte[] outbound = [];
    private bool hungUp;
    private TaskCompletionSource<bool> changed = NewSignal();

    public static ResourceOperationContext Context { get; } = new(new ResourceOperationId("peer", 1), 1, "glenda");

    public ResourceOpenHandle Handle { get; } = new(new ResourceHandle(new ResourceIdentity("peer", "#|", 0), QidType.QTFILE), "peer", NinePConstants.ORDWR, 0);

    public Func<ushort, Tversion, ISerializable?> Version { get; set; } = (tag, version) => new Rversion(tag, version.MSize, "9P2000");

    public bool Disposed { get; private set; }

    public bool RefuseWrites { get; set; }

    public static ushort Tag(byte[] message) => BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(5, 2));

    public static MessageTypes Type(byte[] message) => (MessageTypes)message[4];

    public static byte[] Encode(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }

    // The requests sent after the version, in order, once there are at least count of them.
    public async Task<byte[][]> RequestsAsync(int count)
    {
        using var bound = new CancellationTokenSource(KernelDriver.Bound);
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (requests.Count >= count)
                {
                    return [.. requests];
                }

                wait = changed.Task;
            }

            await wait.WaitAsync(bound.Token);
        }
    }

    // The request at the index, once it has been sent.
    public async Task<byte[]> RequestAsync(int index)
    {
        using var bound = new CancellationTokenSource(KernelDriver.Bound);
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (requests.Count > index)
                {
                    return requests[index];
                }

                wait = changed.Task;
            }

            await wait.WaitAsync(bound.Token);
        }
    }

    public void Send(params byte[][] frames)
    {
        lock (gate)
        {
            foreach (byte[] frame in frames)
            {
                outbound = [.. outbound, .. frame];
            }

            Signal();
        }
    }

    public void Reply(ISerializable reply) => Send(Encode(reply));

    public void HangUp()
    {
        lock (gate)
        {
            hungUp = true;
            Signal();
        }
    }

    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        byte[] message = data.ToArray();
        lock (gate)
        {
            if (RefuseWrites || hungUp)
            {
                throw new IOException(PipeQueue.Hungup);
            }

            if (Type(message) == MessageTypes.Tversion)
            {
                if (Version(Tag(message), new Tversion(message)) is { } reply)
                {
                    outbound = [.. outbound, .. Encode(reply)];
                }
            }
            else
            {
                requests.Add(message);
            }

            Signal();
        }

        return ValueTask.FromResult((uint)message.Length);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (outbound.Length != 0)
                {
                    int length = Math.Min(outbound.Length, (int)count);
                    byte[] read = outbound[..length];
                    outbound = outbound[length..];
                    return read;
                }

                if (hungUp)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }

                wait = changed.Task;
            }

            await wait;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        changed.TrySetResult(true);
        changed = NewSignal();
    }
}
