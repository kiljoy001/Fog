using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devmnt: #M, the trees attached through mount connections, each with a number of its own, as
// mntchan numbers each attach.
internal sealed class MountDevice : IResourceDataOperations, IResourceOpenStatOperations, IResourceWStatOperations
{
    public const string Provider = "mnt";
    private static int mounts;
    private readonly ConcurrentDictionary<string, MountTree> trees = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<ResourceOpenHandle, MountConnection> connections = [];
    private readonly SemaphoreSlim versioning = new(1, 1);

    // Run when a file of a tree is closed, which may have been all that held it.
    public Func<Task> Collect { get; set; } = () => Task.CompletedTask;

    public IReadOnlyCollection<MountTree> Trees => [.. trees.Values];

    // c->mux: the connection on a transport, versioned the first time it is mounted.
    public async Task<MountConnection> ConnectAsync(DescriptorLease transport, IResourceDataOperations device, Func<ResourceOperationContext> context, CancellationToken cancellationToken = default)
    {
        try
        {
            await versioning.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await transport.DisposeAsync();
            throw;
        }

        try
        {
            if (connections.TryGetValue(transport.Handle, out MountConnection? existing))
            {
                await transport.DisposeAsync();
                return existing;
            }

            var connection = new MountConnection(device, transport.Handle, transport, context);
            try
            {
                await connection.VersionAsync(cancellationToken);
            }
            catch
            {
                await transport.DisposeAsync();
                throw;
            }

            connections.Add(transport.Handle, connection);
            return connection;
        }
        finally
        {
            versioning.Release();
        }
    }

    // A tree nothing holds let go, and its connection with it once no other tree is on it.
    public async Task ReleaseAsync(MountTree tree)
    {
        // Stryker disable once Statement,Block : only a collection racing a failed mount's own release lets a tree go twice, and no test orders that race
        if (!trees.TryRemove(tree.Device, out _))
        {
            return;
        }

        try
        {
            await tree.CloseAsync();
        }
        catch (IOException)
        {
            // A connection that has hung up clunks nothing.
        }

        if (!trees.Values.Any(other => other.Connection == tree.Connection))
        {
            await versioning.WaitAsync();
            try
            {
                connections.Remove(tree.Connection.Transport);
            }
            finally
            {
                versioning.Release();
            }

            await tree.Connection.CloseAsync();
        }
    }

    public async Task<MountTree> AttachAsync(MountConnection connection, string user, string aname, int bound)
    {
        MountTree tree = await MountTree.AttachAsync(connection, $"#M{Interlocked.Increment(ref mounts)}", user, aname, bound);
        trees[tree.Device] = tree;
        return tree;
    }

    // The trees attached through the connection speaking on a channel, as cunmount finds a mount by
    // its mchan.
    public IReadOnlyList<MountTree> TreesOn(ResourceOpenHandle channel)
        => [.. trees.Values.Where(tree => tree.Connection.Transport == channel)];

    public async ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
        => await Tree(directory).WalkAsync(directory, name, cancellationToken);

    public async ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => await Tree(directory).ReadDirectoryAsync(directory, cancellationToken);

    // The kernel creates in a mount by opening what it creates, as mntcreate does.
    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new NotSupportedException("a mount creates only by opening what it creates");

    public async ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Tree(directory).CreateAsync(directory, name, permissions, mode, cancellationToken);

    public async ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Tree(resource).OpenAsync(resource, mode, cancellationToken);

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => await Tree(openHandle.Resource).ReadAsync(openHandle, offset, count, cancellationToken);

    public async ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Tree(openHandle.Resource).WriteAsync(openHandle, offset, data, cancellationToken);

    public async ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
        => await Tree(resource).StatAsync(resource, cancellationToken);

    public async ValueTask<ResourceStat> StatOpenAsync(ResourceOpenHandle handle, CancellationToken cancellationToken)
        => await Tree(handle.Resource).StatOpenAsync(handle, cancellationToken);

    public async ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        await Tree(openHandle.Resource).ClunkAsync(openHandle);
        await Collect();
    }

    public async ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => await Tree(resource).RemoveAsync(resource, openHandle, cancellationToken);

    public async ValueTask<uint> WStatAsync(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        await Tree(resource).WStatAsync(resource, stat, cancellationToken);
        return stat.EncodedLength;
    }

    public async ValueTask<uint> WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        await Tree(handle.Resource).WStatOpenAsync(handle, stat, cancellationToken);
        return stat.EncodedLength;
    }

    private MountTree Tree(ResourceHandle resource)
        => trees.TryGetValue(resource.Identity.Device, out MountTree? tree) ? tree : throw new IOException(Errors.NotExist);
}
