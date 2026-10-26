using System.Collections.Concurrent;
using NinePSharp.Constants;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// One attach on a mount connection: devmnt's channels on it, named by qid. The tree keeps the names
// leading from the attached root to each file it has walked to, and holds at most a bound of fids
// for them, letting the least recently used go; a file without a fid is walked to again by its
// names, and is gone if another file is there now. An open file holds a fid of its own.
internal sealed class MountTree
{
    private const int MaxWalk = 16;
    private readonly object gate = new();
    private readonly Dictionary<ulong, Entry> entries = [];
    private readonly LinkedList<ulong> recent = new();
    private readonly ConcurrentDictionary<string, (uint Fid, uint IoUnit)> opened = new(StringComparer.Ordinal);
    private readonly int bound;

    private MountTree(MountConnection connection, string device, uint fid, Qid root, int bound)
    {
        Connection = connection;
        Device = device;
        RootFid = fid;
        Root = Handle(root);
        this.bound = bound;
    }

    public MountConnection Connection { get; }

    public string Device { get; }

    public uint RootFid { get; }

    public ResourceHandle Root { get; }

    // Whether a file of the tree is open, which keeps it, as an open chan keeps its Mnt.
    public bool HasOpenFiles => !opened.IsEmpty;

    // Whether the tree is between its attach and its mount, which nothing yet holds it by.
    // Stryker disable once Boolean : only a collection that runs between a mount's attach and its bind sees it
    public bool Pending { get; set; } = true;

    // mntattach: a new fid on the connection's root for this user and attach name.
    public static async Task<MountTree> AttachAsync(MountConnection connection, string device, string user, string aname, int bound)
    {
        uint fid = connection.NewFid();
        byte[] reply = await connection.RpcAsync(tag => new Tattach(tag, fid, NinePConstants.NoFid, user, aname), MessageTypes.Rattach, CancellationToken.None);
        return new MountTree(connection, device, fid, new Rattach(reply).Qid, bound);
    }

    public ResourceHandle Handle(Qid qid) => new(new ResourceIdentity(MountDevice.Provider, Device, qid.Path), qid.Type, qid.Version);

    // mntwalk, one name: the child keeps the fid the walk made, unless it already has one. A walk
    // the server refuses is one that found nothing, as mntwalk returns nil for it.
    public async Task<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        await using Use use = await UseAsync(directory, cancellationToken);
        uint fid = Connection.NewFid();
        Rwalk walk;
        try
        {
            walk = new Rwalk(await Connection.RpcAsync(tag => new Twalk(tag, use.Fid, fid, [name]), MessageTypes.Rwalk, cancellationToken));
        }
        catch (MountErrorException)
        {
            return null;
        }

        if (walk.Wqid.Length == 0)
        {
            return null;
        }

        ResourceHandle child = Handle(walk.Wqid[0]);
        await KeepAsync(child, [.. Names(directory), name], fid);
        return child;
    }

    public async Task<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
    {
        (uint fid, uint iounit) = await OpenFidAsync(directory, NinePConstants.OREAD, cancellationToken);
        var records = new List<byte>();
        try
        {
            while (true)
            {
                var read = new Rread(await Connection.RpcAsync(tag => new Tread(tag, fid, (ulong)records.Count, iounit), MessageTypes.Rread, cancellationToken));
                if (read.Count == 0)
                {
                    break;
                }

                records.AddRange(read.Data.Span);
            }
        }
        finally
        {
            await ClunkFidAsync(fid);
        }

        string[] names = Names(directory);
        var entries = new List<ResourceDirectoryEntry>();
        byte[] all = [.. records];
        for (int offset = 0; offset < all.Length;)
        {
            var stat = new Stat(all, ref offset);
            ResourceHandle child = Handle(stat.Qid);
            Remember(child, [.. names, stat.Name]);
            entries.Add(new ResourceDirectoryEntry(stat.Name, child));
        }

        return entries;
    }

    public async Task<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        await using Use use = await UseAsync(resource, cancellationToken);
        return await StatFidAsync(use.Fid, cancellationToken);
    }

    public async Task<ResourceStat> StatOpenAsync(ResourceOpenHandle handle, CancellationToken cancellationToken)
        => await StatFidAsync(Opened(handle).Fid, cancellationToken);

    // mntopen: a clone of the file's fid, opened, which the open file keeps until it is closed.
    public async Task<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, CancellationToken cancellationToken)
    {
        (uint fid, uint iounit) = await OpenFidAsync(resource, mode, cancellationToken);
        return Open(resource, fid, iounit, mode);
    }

    // mntcreate: a clone of the directory's fid becomes the created file, open.
    public async Task<ResourceOpenHandle> CreateAsync(ResourceHandle directory, string name, uint permissions, byte mode, CancellationToken cancellationToken)
    {
        uint fid = await CloneAsync(directory, cancellationToken);
        try
        {
            var created = new Rcreate(await Connection.RpcAsync(tag => new Tcreate(tag, fid, name, permissions, mode), MessageTypes.Rcreate, cancellationToken));
            ResourceHandle file = Handle(created.Qid);
            Remember(file, [.. Names(directory), name]);
            return Open(file, fid, created.Iounit, mode);
        }
        catch
        {
            await ClunkFidAsync(fid);
            throw;
        }
    }

    // mntrdwr: pieces of at most the iounit, until one comes up short; one is asked for even of
    // nothing.
    public async Task<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle handle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        (uint fid, uint iounit) = Opened(handle);
        var data = new List<byte>();
        do
        {
            uint piece = Math.Min(count - (uint)data.Count, iounit);
            var read = new Rread(await Connection.RpcAsync(tag => new Tread(tag, fid, offset + (ulong)data.Count, piece), MessageTypes.Rread, cancellationToken));
            data.AddRange(read.Data.Span[..(int)Math.Min(read.Count, piece)]);
            if (read.Count < piece)
            {
                break;
            }
        }
        while (data.Count < count);

        return data.ToArray();
    }

    public async Task<uint> WriteAsync(ResourceOpenHandle handle, ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        (uint fid, uint iounit) = Opened(handle);
        uint done = 0;
        do
        {
            ReadOnlyMemory<byte> piece = data.Slice((int)done, (int)Math.Min((uint)data.Length - done, iounit));
            var wrote = new Rwrite(await Connection.RpcAsync(tag => new Twrite(tag, fid, offset + done, piece), MessageTypes.Rwrite, cancellationToken));
            done += Math.Min(wrote.Count, (uint)piece.Length);
            if (wrote.Count < piece.Length)
            {
                break;
            }
        }
        while (done < data.Length);

        return done;
    }

    // The tree let go: the fids it holds, its root's and the walked files', clunked.
    public async Task CloseAsync()
    {
        uint[] held;
        lock (gate)
        {
            held = [.. entries.Values.Where(e => e.Fid is not null && e.Uses == 0).Select(e => e.Fid!.Value), RootFid];
        }

        foreach (uint fid in held)
        {
            await ClunkFidAsync(fid);
        }
    }

    public async Task ClunkAsync(ResourceOpenHandle handle)
    {
        if (opened.TryRemove(handle.HandleId, out (uint Fid, uint IoUnit) open))
        {
            await Connection.RpcAsync(tag => new Tclunk(tag, open.Fid), MessageTypes.Rclunk, CancellationToken.None);
        }
    }

    // mntremove: Tremove clunks the fid it names, the open file's or a clone of the file's.
    public async Task RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, CancellationToken cancellationToken)
    {
        uint fid;
        if (openHandle is not null)
        {
            fid = opened.TryRemove(openHandle.HandleId, out (uint Fid, uint IoUnit) open) ? open.Fid : throw new IOException(Errors.BadUseFd);
        }
        else
        {
            fid = await CloneAsync(resource, cancellationToken);
        }

        await Connection.RpcAsync(tag => new Tremove(tag, fid), MessageTypes.Rremove, cancellationToken);
        await ForgetAsync(resource);
    }

    public async Task WStatAsync(ResourceHandle resource, ResourceWStat stat, CancellationToken cancellationToken)
    {
        await using Use use = await UseAsync(resource, cancellationToken);
        await WStatFidAsync(use.Fid, stat, cancellationToken);
    }

    public Task WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, CancellationToken cancellationToken)
        => WStatFidAsync(Opened(handle).Fid, stat, cancellationToken);

    private async Task WStatFidAsync(uint fid, ResourceWStat stat, CancellationToken cancellationToken)
    {
        var record = new Stat(0, stat.Type, stat.Device, stat.Qid, stat.Mode, stat.AccessTime, stat.ModificationTime, stat.Length, stat.Name, stat.User, stat.Group, stat.LastModifier);
        await Connection.RpcAsync(tag => new Twstat(tag, fid, record), MessageTypes.Rwstat, cancellationToken);
    }

    private async Task<ResourceStat> StatFidAsync(uint fid, CancellationToken cancellationToken)
    {
        Stat stat = new Rstat(await Connection.RpcAsync(tag => new Tstat(tag, fid), MessageTypes.Rstat, cancellationToken)).Stat;
        return new ResourceStat(Handle(stat.Qid), stat.Name, stat.Mode, stat.Atime, stat.Mtime, stat.Length, stat.Uid, stat.Gid, stat.Muid);
    }

    private async Task<(uint Fid, uint IoUnit)> OpenFidAsync(ResourceHandle resource, byte mode, CancellationToken cancellationToken)
    {
        uint fid = await CloneAsync(resource, cancellationToken);
        try
        {
            var open = new Ropen(await Connection.RpcAsync(tag => new Topen(tag, fid, mode), MessageTypes.Ropen, cancellationToken));
            return (fid, IoUnit(open.Iounit));
        }
        catch
        {
            await ClunkFidAsync(fid);
            throw;
        }
    }

    private async Task<uint> CloneAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        await using Use use = await UseAsync(resource, cancellationToken);
        uint fid = Connection.NewFid();
        await Connection.RpcAsync(tag => new Twalk(tag, use.Fid, fid, []), MessageTypes.Rwalk, cancellationToken);
        return fid;
    }

    private ResourceOpenHandle Open(ResourceHandle resource, uint fid, uint iounit, byte mode)
    {
        uint unit = IoUnit(iounit);
        string id = $"{Device}/{fid}";
        opened[id] = (fid, unit);
        return new ResourceOpenHandle(resource, id, mode, unit);
    }

    // mntopencreate: no iounit, or one past what a message carries, is what a message carries.
    private uint IoUnit(uint iounit)
    {
        uint most = (uint)(Connection.MessageSize - MountConnection.HeaderRoom);
        return iounit == 0 ? most : Math.Min(iounit, most);
    }

    private (uint Fid, uint IoUnit) Opened(ResourceOpenHandle handle)
        => opened.TryGetValue(handle.HandleId, out (uint Fid, uint IoUnit) open) ? open : throw new IOException(Errors.BadUseFd);

    // A fid for the file, walked to again by its names if it has none, held from being let go
    // while it is in use.
    private async Task<Use> UseAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        if (resource.Identity.Path == Root.Identity.Path)
        {
            return new Use(this, null, RootFid);
        }

        string[] names;
        lock (gate)
        {
            if (!entries.TryGetValue(resource.Identity.Path, out Entry? entry))
            {
                throw new IOException(Errors.NotExist);
            }

            if (entry.Fid is { } held)
            {
                entry.Uses++;
                Touch(entry, resource.Identity.Path);
                return new Use(this, entry, held);
            }

            names = entry.Names;
        }

        uint fid = await WalkFromRootAsync(names, resource, cancellationToken);
        await KeepAsync(resource, names, fid);
        lock (gate)
        {
            Entry entry = entries[resource.Identity.Path];
            entry.Uses++;
            return new Use(this, entry, entry.Fid!.Value);
        }
    }

    private async Task<uint> WalkFromRootAsync(string[] names, ResourceHandle resource, CancellationToken cancellationToken)
    {
        uint fid = Connection.NewFid();
        uint from = RootFid;
        Qid last = default;
        for (int at = 0; at < names.Length; at += MaxWalk)
        {
            string[] part = names[at..Math.Min(at + MaxWalk, names.Length)];
            var walk = new Rwalk(await Connection.RpcAsync(tag => new Twalk(tag, from, fid, part), MessageTypes.Rwalk, cancellationToken));
            if (walk.Wqid.Length < part.Length)
            {
                if (from == fid)
                {
                    await ClunkFidAsync(fid);
                }

                throw new IOException(Errors.NotExist);
            }

            from = fid;
            last = walk.Wqid[^1];
        }

        if (last.Path != resource.Identity.Path)
        {
            await ClunkFidAsync(fid);
            throw new IOException(Errors.NotExist);
        }

        return fid;
    }

    // Keeps a walked fid for the file, or clunks it if the file has one already; then lets the
    // least recently used fids not in use go, down to the bound.
    private async Task KeepAsync(ResourceHandle resource, string[] names, uint fid)
    {
        var surplus = new List<uint>();
        lock (gate)
        {
            if (!entries.TryGetValue(resource.Identity.Path, out Entry? entry))
            {
                entry = new Entry(names);
                entries[resource.Identity.Path] = entry;
            }

            if (entry.Fid is null)
            {
                entry.Fid = fid;
                Touch(entry, resource.Identity.Path);
            }
            else
            {
                surplus.Add(fid);
            }

            for (LinkedListNode<ulong>? node = recent.Last; recent.Count > bound && node is not null;)
            {
                LinkedListNode<ulong>? previous = node.Previous;
                Entry old = entries[node.Value];
                if (old.Uses == 0)
                {
                    surplus.Add(old.Fid!.Value);
                    old.Fid = null;
                    old.Recent = null;
                    recent.Remove(node);
                }

                node = previous;
            }
        }

        foreach (uint extra in surplus)
        {
            await ClunkFidAsync(extra);
        }
    }

    private void Remember(ResourceHandle resource, string[] names)
    {
        lock (gate)
        {
            entries.TryAdd(resource.Identity.Path, new Entry(names));
        }
    }

    private async Task ForgetAsync(ResourceHandle resource)
    {
        uint? fid;
        lock (gate)
        {
            if (!entries.Remove(resource.Identity.Path, out Entry? entry))
            {
                return;
            }

            entry.Forgotten = true;
            fid = entry.Uses == 0 ? entry.Fid : null;
            if (entry.Recent is not null)
            {
                recent.Remove(entry.Recent);
            }
        }

        if (fid is { } held)
        {
            await ClunkFidAsync(held);
        }
    }

    private void Touch(Entry entry, ulong path)
    {
        if (entry.Recent is not null)
        {
            recent.Remove(entry.Recent);
        }

        entry.Recent = recent.AddFirst(path);
    }

    private string[] Names(ResourceHandle resource)
    {
        if (resource.Identity.Path == Root.Identity.Path)
        {
            return [];
        }

        lock (gate)
        {
            return entries.TryGetValue(resource.Identity.Path, out Entry? entry) ? entry.Names : throw new IOException(Errors.NotExist);
        }
    }

    private async Task ClunkFidAsync(uint fid)
    {
        try
        {
            await Connection.RpcAsync(tag => new Tclunk(tag, fid), MessageTypes.Rclunk, CancellationToken.None);
        }
        catch (IOException)
        {
            // An Rerror to Tclunk still clunks the fid.
        }
    }

    // The fid of a file forgotten while in use is let go by its last use.
    private async ValueTask ReleaseAsync(Entry entry)
    {
        uint? fid;
        lock (gate)
        {
            entry.Uses--;
            fid = entry.Forgotten && entry.Uses == 0 ? entry.Fid : null;
        }

        if (fid is { } held)
        {
            await ClunkFidAsync(held);
        }
    }

    private sealed class Entry(string[] names)
    {
        public string[] Names { get; } = names;

        public uint? Fid { get; set; }

        public int Uses { get; set; }

        public LinkedListNode<ulong>? Recent { get; set; }

        public bool Forgotten { get; set; }
    }

    private sealed class Use(MountTree tree, Entry? entry, uint fid) : IAsyncDisposable
    {
        public uint Fid { get; } = fid;

        public ValueTask DisposeAsync() => entry is null ? ValueTask.CompletedTask : tree.ReleaseAsync(entry);
    }
}
