using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

internal sealed class RamFs : IResourceDataOperations
{
    private readonly object gate = new();
    private readonly string provider;
    private readonly string device;
    private readonly string owner;
    private readonly Dictionary<ulong, Node> nodes = new();

    public RamFs(string provider, string device, string owner)
    {
        this.provider = provider;
        this.device = device;
        this.owner = owner;
        nodes.Add(0, new Node(device, null, (uint)NinePConstants.FileMode9P.DMDIR | 0777, owner));
    }

    public string Provider => provider;

    public ResourceHandle Root => Handle(0, nodes[0]);

    public RamFs Copy()
    {
        lock (gate)
        {
            var copy = new RamFs(provider, device, owner);
            foreach (var (path, node) in nodes)
            {
                copy.nodes[path] = node.Copy();
            }

            return copy;
        }
    }

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ulong? found = Find(nodes[directory.Identity.Path], name);
            return ValueTask.FromResult(found is { } path ? Handle(path, nodes[path]) : null);
        }
    }

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            IReadOnlyList<ResourceDirectoryEntry> entries = nodes[directory.Identity.Path].Children
                .Select(path => new ResourceDirectoryEntry(nodes[path].Name, Handle(path, nodes[path])))
                .ToArray();
            return ValueTask.FromResult(entries);
        }
    }

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
    {
        uint permissions = directoryEntry ? (uint)NinePConstants.FileMode9P.DMDIR | 0777 : 0666;
        lock (gate)
        {
            return ValueTask.FromResult(Add(directory.Identity.Path, name, permissions, owner));
        }
    }

    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if ((mode & NinePConstants.OTRUNC) != 0)
            {
                nodes[resource.Identity.Path].Data = Array.Empty<byte>();
            }

            return ValueTask.FromResult(new ResourceOpenHandle(resource, HandleId(context), mode, 0));
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            byte[] data = nodes[openHandle.Resource.Identity.Path].Data;
            int start = (int)Math.Min(offset, (ulong)data.Length);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(data.AsSpan(start, Math.Min((int)count, data.Length - start)).ToArray());
        }
    }

    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Node node = nodes[openHandle.Resource.Identity.Path];
            byte[] contents = node.Data;
            Array.Resize(ref contents, Math.Max(contents.Length, (int)offset + data.Length));
            node.Data = contents;
            data.Span.CopyTo(node.Data.AsSpan((int)offset));
            return ValueTask.FromResult((uint)data.Length);
        }
    }

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Node node = nodes[resource.Identity.Path];
            return ValueTask.FromResult(new ResourceStat(resource, node.Name, node.Permissions, 0, 0, (ulong)node.Data.Length, node.Owner, node.Owner, node.Owner));
        }
    }

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (Find(nodes[directory.Identity.Path], name) is not null)
            {
                throw new ResourceCreateRejectedException("file already exists");
            }

            ResourceHandle created = Add(directory.Identity.Path, name, permissions, context.User);
            return ValueTask.FromResult(new ResourceOpenHandle(created, HandleId(context), mode, 0));
        }
    }

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    // Removed nodes stay reachable through handles that still name them, as lib9p's refcounted Files do.
    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Node node = nodes[resource.Identity.Path];
            if (node.Children.Count != 0)
            {
                throw new IOException("has children");
            }

            nodes[node.Parent!.Value].Children.Remove(resource.Identity.Path);
            return ValueTask.CompletedTask;
        }
    }

    private static string HandleId(ResourceOperationContext context)
        => $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";

    private ResourceHandle Add(ulong parent, string name, uint permissions, string user)
    {
        ulong path = (ulong)nodes.Count;
        nodes.Add(path, new Node(name, parent, permissions, user));
        nodes[parent].Children.Add(path);
        return Handle(path, nodes[path]);
    }

    private ulong? Find(Node directory, string name)
    {
        foreach (ulong path in directory.Children)
        {
            if (nodes[path].Name == name)
            {
                return path;
            }
        }

        return null;
    }

    private ResourceHandle Handle(ulong path, Node node)
        => new(new ResourceIdentity(provider, device, path), (node.Permissions & (uint)NinePConstants.FileMode9P.DMDIR) != 0 ? QidType.QTDIR : QidType.QTFILE);

    private sealed class Node(string name, ulong? parent, uint permissions, string owner)
    {
        public string Name { get; } = name;

        public ulong? Parent { get; } = parent;

        public uint Permissions { get; } = permissions;

        public string Owner { get; } = owner;

        public byte[] Data { get; set; } = Array.Empty<byte>();

        public List<ulong> Children { get; private init; } = new();

        public Node Copy() => new(Name, Parent, Permissions, Owner) { Data = Data.ToArray(), Children = Children.ToList() };
    }
}
