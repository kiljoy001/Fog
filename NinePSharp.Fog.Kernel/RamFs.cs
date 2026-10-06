using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

internal sealed class RamFs : IResourceDataOperations, IResourceWStatOperations
{
    // ramfs's MAXFSIZE: as many 64 KiB blocks as its block table can index.
    private const ulong MaxFileSize = (0x7fffffffUL / 8) * BlockSize;
    private const int BlockSize = 64 * 1024;
    private readonly object gate = new();
    private readonly string provider;
    private readonly string device;
    private readonly string owner;
    private readonly bool changeable;
    private readonly TimeProvider clock;
    private readonly Dictionary<ulong, Node> nodes = new();

    // An unchangeable instance refuses wstat as devenv does, for /env.
    public RamFs(string provider, string device, string owner, bool changeable = true, TimeProvider? clock = null)
    {
        this.provider = provider;
        this.device = device;
        this.owner = owner;
        this.changeable = changeable;
        this.clock = clock ?? TimeProvider.System;
        nodes.Add(0, new Node(device, null, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_111, owner, owner, Now));
    }

    public string Provider => provider;

    public ResourceHandle Root => Handle(0, nodes[0]);

    private uint Now => (uint)clock.GetUtcNow().ToUnixTimeSeconds();

    public RamFs Copy()
    {
        lock (gate)
        {
            var copy = new RamFs(provider, device, owner, changeable, clock);
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
        uint permissions = directoryEntry ? (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_111 : 0b110_110_110;
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
                Truncate(nodes[resource.Identity.Path], 0);
            }

            return ValueTask.FromResult(new ResourceOpenHandle(resource, HandleId(context), mode, 0));
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Node node = nodes[openHandle.Resource.Identity.Path];
            if (count == 0 || offset >= node.Length || node.Blocks is null)
            {
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            }

            if (offset + count > MaxFileSize)
            {
                throw new IOException(Errors.BadOffset);
            }

            var reply = new byte[Math.Min(offset + count, node.Length) - offset];
            for (int done = 0; done < reply.Length;)
            {
                ulong at = offset + (ulong)done;
                int within = (int)(at % BlockSize);
                int n = Math.Min(BlockSize - within, reply.Length - done);
                if (node.Blocks.TryGetValue(at / BlockSize, out byte[]? block) && within < block.Length)
                {
                    n = Math.Min(block.Length - within, n);
                    block.AsSpan(within, n).CopyTo(reply.AsSpan(done));
                }

                done += n;
            }

            node.AccessTime = Now;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(reply);
        }
    }

    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Node node = nodes[openHandle.Resource.Identity.Path];
            if (data.IsEmpty)
            {
                return ValueTask.FromResult(0u);
            }

            ulong top = offset + (ulong)data.Length;
            if (top > MaxFileSize)
            {
                throw new IOException(Errors.BadOffset);
            }

            node.Blocks ??= new();
            for (int done = 0; done < data.Length;)
            {
                ulong at = offset + (ulong)done;
                int within = (int)(at % BlockSize);
                int n = Math.Min(BlockSize - within, data.Length - done);
                node.Blocks.TryGetValue(at / BlockSize, out byte[]? block);
                Array.Resize(ref block, Math.Max(block?.Length ?? 0, within + n));
                node.Blocks[at / BlockSize] = block;

                data.Span.Slice(done, n).CopyTo(block.AsSpan(within));
                done += n;
            }

            node.Length = Math.Max(node.Length, top);
            node.AccessTime = node.ModificationTime = Now;
            return ValueTask.FromResult((uint)data.Length);
        }
    }

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Node node = nodes[resource.Identity.Path];
            return ValueTask.FromResult(new ResourceStat(resource, node.Name, node.Permissions, node.AccessTime, node.ModificationTime, node.Length, node.Owner, node.Group, node.Owner));
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

    // ramfs removes a file opened ORCLOSE as its fid goes, unless it is the root or has children.
    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Node node = nodes[openHandle.Resource.Identity.Path];
            if ((openHandle.Mode & NinePConstants.ORCLOSE) != 0 && node.Parent is { } parent && node.Children.Count == 0)
            {
                nodes[parent].Children.Remove(openHandle.Resource.Identity.Path);
            }

            return ValueTask.CompletedTask;
        }
    }

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

    public ValueTask<uint> WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => WStatAsync(handle.Resource, stat, context, cancellationToken);

    // ramfs's fswstat: every check before any change; all ones or an empty string leaves a field alone.
    public ValueTask<uint> WStatAsync(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (!changeable)
        {
            throw new ResourceWStatRejectedException(Errors.Permission);
        }

        lock (gate)
        {
            Node file = nodes[resource.Identity.Path];
            string user = context.User;
            bool resize = stat.Length != ulong.MaxValue && stat.Length != file.Length;
            if (resize)
            {
                CheckResize(file, stat.Length, user);
            }

            CheckRename(file, stat.Name, user);
            CheckOwner(file, stat, user);
            Apply(file, stat, resize);
            return ValueTask.FromResult(stat.EncodedLength);
        }
    }

    private static string HandleId(ResourceOperationContext context)
        => $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";

    // lib9p's hasperm for AWRITE: each user leads a group of its own name.
    private static bool CanWrite(Node file, string user)
        => (file.Permissions & 0b000_000_010) != 0
            || (user == file.Owner && (file.Permissions & 0b010_000_000) != 0)
            || (user == file.Group && (file.Permissions & 0b000_010_000) != 0);

    private static void CheckResize(Node file, ulong length, string user)
    {
        if (length > MaxFileSize)
        {
            throw new ResourceWStatRejectedException(Errors.BadOffset);
        }

        if (!CanWrite(file, user) || file.IsDirectory)
        {
            throw new ResourceWStatRejectedException(Errors.Permission);
        }
    }

    // The mode needs the owner or the group, the group the owner; ramfs's case of a group leader
    // giving the file to itself cannot happen, as its comment says.
    private static void CheckOwner(Node file, ResourceWStat stat, string user)
    {
        bool mode = stat.Mode != uint.MaxValue && stat.Mode != file.Permissions;
        bool group = stat.Group.Length != 0 && stat.Group != file.Group;
        if ((mode && user != file.Owner && user != file.Group) || (group && user != file.Owner))
        {
            throw new ResourceWStatRejectedException("not owner");
        }
    }

    // truncfile: drops the blocks past the new length, keeping the start of the one it ends in.
    private static void Truncate(Node file, ulong length)
    {
        if (file.Blocks is { } blocks)
        {
            ulong first = length / BlockSize;
            int within = (int)(length % BlockSize);
            if (within != 0 && blocks.TryGetValue(first, out byte[]? block))
            {
                blocks[first] = block.AsSpan(0, Math.Min(within, block.Length)).ToArray();
                first++;
            }

            foreach (ulong index in blocks.Keys.Where(index => index >= first).ToList())
            {
                blocks.Remove(index);
            }

            if (length == 0)
            {
                file.Blocks = null;
            }
        }

        file.Length = length;
    }

    private void Apply(Node file, ResourceWStat stat, bool resize)
    {
        if (stat.Mode != uint.MaxValue)
        {
            file.Permissions = stat.Mode;
        }

        if (stat.Name.Length != 0)
        {
            file.Name = stat.Name;
        }

        if (resize)
        {
            Truncate(file, stat.Length);
        }

        file.AccessTime = file.ModificationTime = Now;
        if (stat.ModificationTime != uint.MaxValue)
        {
            file.ModificationTime = stat.ModificationTime;
        }
    }

    // To rename, the parent must be writable and must not hold the name already; the root has no parent.
    private void CheckRename(Node file, string name, string user)
    {
        if (name.Length == 0 || name == file.Name)
        {
            return;
        }

        if (file.Parent is not { } parent || !CanWrite(nodes[parent], user))
        {
            throw new ResourceWStatRejectedException(Errors.Permission);
        }

        if (Find(nodes[parent], name) is not null)
        {
            throw new ResourceWStatRejectedException("file already exists");
        }
    }

    private ResourceHandle Add(ulong parent, string name, uint permissions, string user)
    {
        ulong path = (ulong)nodes.Count;
        nodes.Add(path, new Node(name, parent, permissions, user, nodes[parent].Group, Now));
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

    private sealed class Node(string name, ulong? parent, uint permissions, string owner, string group, uint time)
    {
        public string Name { get; set; } = name;

        public ulong? Parent { get; } = parent;

        public uint Permissions { get; set; } = permissions;

        public string Owner { get; } = owner;

        public string Group { get; } = group;

        public uint AccessTime { get; set; } = time;

        public uint ModificationTime { get; set; } = time;

        public bool IsDirectory => (Permissions & (uint)NinePConstants.FileMode9P.DMDIR) != 0;

        public ulong Length { get; set; }

        // ramfs's block table: 64 KiB blocks by index, none until the first write.
        public Dictionary<ulong, byte[]>? Blocks { get; set; }

        public List<ulong> Children { get; private init; } = new();

        public Node Copy() => new(Name, Parent, Permissions, Owner, Group, AccessTime)
        {
            Length = Length,
            Blocks = Blocks?.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray()),
            Children = Children.ToList(),
            ModificationTime = ModificationTime,
        };
    }
}
