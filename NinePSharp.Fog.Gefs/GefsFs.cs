using System.Buffers.Binary;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Gefs;

// fs.c: a tree a label names, served as files. A directory entry lives under its parent's qid and
// its name; unlike gefs, every entry also keeps the key of its directory entry under its own qid,
// so a handle alone finds it. A file's data lives in blocks under its qid and offset.
internal sealed class GefsFs : IResourceDataOperations, IResourceWStatOperations
{
    private const long B = Format.BlockSize;
    private const int Read = 4;
    private const int Write = 2;
    private const int Exec = 1;
    private const uint Directory = 0x80000000;
    private const uint Append = 0x40000000;
    private const uint Exclusive = 0x20000000;
    private const uint Temporary = 0x04000000;
    private const uint MountPoint = 0x10000000;
    private const uint Authentication = 0x08000000;
    private const uint Settable = Directory | Append | Exclusive | Temporary | 0b111_111_111;

    // Each upsert carries at most this many block changes, well within an empty buffer.
    private const int Batch = 64;

    private readonly object gate = new();
    private readonly Store store;
    private readonly Tree tree;
    private readonly string device;
    private readonly string owner;
    private readonly Func<string, string, bool> inGroup;
    private readonly TimeProvider clock;
    private readonly HashSet<long> exclusive = [];

    // inGroup says whether a user is in a group; every user is in the group of its own name.
    public GefsFs(Store store, string label, string device, Func<string, string, bool> inGroup, TimeProvider? clock = null)
    {
        this.store = store;
        this.device = device;
        this.inGroup = inGroup;
        this.clock = clock ?? TimeProvider.System;
        tree = store.Mount(label);
        owner = Find(0).Dir.User;
    }

    public ResourceHandle Root
    {
        get
        {
            lock (gate)
            {
                return Handle(Find(0).Dir);
            }
        }
    }

    private long Now => Store.Nanoseconds(clock);

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            long qid = Qid(directory);
            if (name == "..")
            {
                long parent = Parent(qid);
                return ValueTask.FromResult<ResourceHandle?>(Handle(Find(parent == -1 ? qid : parent).Dir));
            }

            return ValueTask.FromResult(tree.Lookup(Keys.Entry(qid, name)) is { } value ? Handle(Dir.Read(Keys.Entry(qid, name), value)) : null);
        }
    }

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var prefix = new byte[9];
            prefix[0] = (byte)KeyType.Entry;
            BinaryPrimitives.WriteInt64BigEndian(prefix.AsSpan(1), Qid(directory));
            var scan = new Scan(prefix);
            scan.Enter(tree);
            var entries = new List<ResourceDirectoryEntry>();
            while (scan.Next())
            {
                Dir d = Dir.Read(scan.Key, scan.Value);
                entries.Add(new ResourceDirectoryEntry(d.Name, Handle(d)));
            }

            return ValueTask.FromResult<IReadOnlyList<ResourceDirectoryEntry>>(entries);
        }
    }

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return ValueTask.FromResult(Handle(Create(Qid(directory), name, directoryEntry ? Directory | 0b111_111_111 : 0b110_110_110, owner)));
        }
    }

    // fscreate: refused as gefs refuses, then an entry owned by its creator in the directory's group.
    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Dir d;
            try
            {
                d = Create(Qid(directory), name, permissions, context.User);
            }
            catch (IOException refused)
            {
                throw new ResourceCreateRejectedException(refused.Message);
            }

            if ((d.Qid.Type & (byte)QidType.QTEXCL) != 0)
            {
                exclusive.Add(d.Qid.Path);
            }

            return ValueTask.FromResult(new ResourceOpenHandle(Handle(d), HandleId(context), mode, 0));
        }
    }

    // fsopen: a directory opens only to read; removing on close needs what removing needs; opening
    // with truncation empties the file. An exclusive file opens once at a time.
    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var (key, d) = Find(Qid(resource));
            int bits = Bits(mode);
            if (IsDirectory(d) && (bits & Write) != 0)
            {
                throw new IOException("permission denied");
            }

            if ((d.Qid.Type & (byte)QidType.QTEXCL) != 0 && exclusive.Contains(d.Qid.Path))
            {
                throw new IOException("open/create -- file is locked");
            }

            if ((mode & NinePConstants.ORCLOSE) != 0)
            {
                CanRemove(d, context.User);
            }

            if (!Allowed(d, context.User, bits))
            {
                throw new IOException("permission denied");
            }

            if ((d.Qid.Type & (byte)QidType.QTEXCL) != 0)
            {
                exclusive.Add(d.Qid.Path);
            }

            if ((mode & NinePConstants.OTRUNC) != 0 && (d.Mode & Append) == 0)
            {
                Change(key, Clear(d.Qid.Path, 0, d.Length), new WstatChange(Length: 0, Muid: context.User));
                d = Find(d.Qid.Path).Dir;
            }

            return ValueTask.FromResult(new ResourceOpenHandle(Handle(d), HandleId(context), mode, 0));
        }
    }

    // readfile: up to the file's end, holes as zeros.
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Dir d = Find(Qid(openHandle.Resource)).Dir;
            if (IsDirectory(d) || offset >= (ulong)d.Length)
            {
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            }

            var reply = new byte[(int)Math.Min(count, (ulong)d.Length - offset)];
            for (int done = 0; done < reply.Length;)
            {
                long at = (long)offset + done;
                int within = (int)(at % B);
                int n = Math.Min((int)B - within, reply.Length - done);
                if (tree.Lookup(Keys.Data(d.Qid.Path, at - within)) is { } bp)
                {
                    store.Allocator.ReadData(Bptr.Read(bp)).AsSpan(within, n).CopyTo(reply.AsSpan(done));
                }

                done += n;
            }

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(reply);
        }
    }

    // fswrite and writeb: each block touched written anew, keeping the bytes around the write from the
    // block it replaces; then the length, modification time and last modifier.
    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if ((Bits(openHandle.Mode) & Write) == 0)
            {
                throw new IOException("resource in use");
            }

            var (key, d) = Find(Qid(openHandle.Resource));
            long start = (d.Mode & Append) != 0 ? d.Length : (long)offset;
            var changes = new List<Message>();
            for (int done = 0; done < data.Length;)
            {
                long at = start + done;
                long fb = at - (at % B);
                int within = (int)(at - fb);
                int n = Math.Min((int)B - within, data.Length - done);
                Blk b = store.Allocator.New(BlockType.Data, tree.Gen);
                if (fb < d.Length && n != B && tree.Lookup(Keys.Data(d.Qid.Path, fb)) is { } old)
                {
                    store.Allocator.ReadData(Bptr.Read(old)).CopyTo(b.Buffer, 0);
                }

                data.Span.Slice(done, n).CopyTo(b.Buffer.AsSpan(within));
                store.Allocator.Enqueue(b);
                var bp = new byte[Format.PointerSize];
                b.Pointer.Write(bp);
                changes.Add(new Message(MessageOp.Insert, Keys.Data(d.Qid.Path, fb), bp));
                done += n;
            }

            long end = start + data.Length;
            Change(key, changes, new WstatChange(Length: end > d.Length ? end : null, Mtime: Now, Muid: context.User));
            return ValueTask.FromResult((uint)data.Length);
        }
    }

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Dir d = Find(Qid(resource)).Dir;
            return ValueTask.FromResult(new ResourceStat(Handle(d), d.Name.Length == 0 ? "/" : d.Name, d.Mode, Seconds(d.Atime), Seconds(d.Mtime), (ulong)d.Length, d.User, d.Group, d.Muid));
        }
    }

    // A file opened to be removed on close goes now; it was checked when it was opened.
    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            long qid = Qid(openHandle.Resource);
            exclusive.Remove(qid);
            if ((openHandle.Mode & NinePConstants.ORCLOSE) != 0)
            {
                Remove(Find(qid));
            }

            return ValueTask.CompletedTask;
        }
    }

    // fsremove: an empty directory or a file, by a user who may write its directory; not the root.
    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var found = Find(Qid(resource));
            CanRemove(found.Dir, context.User);
            Remove(found);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<uint> WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => WStatAsync(handle.Resource, stat, context, cancellationToken);

    // fswstat: every field checked, then every permission, before anything changes. A wstat that
    // names nothing asks for a sync, and commits the store.
    public ValueTask<uint> WStatAsync(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var (key, d) = Find(Qid(resource));
            string user = context.User;
            if (stat.Qid.Path != ulong.MaxValue || stat.Qid.Version != uint.MaxValue || stat.Qid.Type != (QidType)0xff || stat.Type != ushort.MaxValue || stat.Device != uint.MaxValue)
            {
                throw Refused("wstat -- attempt to change qid");
            }

            bool rename = stat.Name.Length != 0 && stat.Name != d.Name;
            if (stat.Name.Length != 0 && System.Text.Encoding.UTF8.GetByteCount(stat.Name) > Format.MaxName)
            {
                throw Refused("name too long");
            }

            if (rename)
            {
                if (NameError(stat.Name) is { } error)
                {
                    throw Refused(error);
                }

                if (tree.Lookup(Keys.Entry(Parent(d.Qid.Path), stat.Name)) is not null)
                {
                    throw Refused("create/wstat -- file exists");
                }
            }

            long? length = null;
            if (stat.Length != ulong.MaxValue)
            {
                if (IsDirectory(d) || stat.Length > long.MaxValue)
                {
                    throw Refused("wstat -- attempt to make length negative");
                }

                length = stat.Length == (ulong)d.Length ? null : (long)stat.Length;
            }

            uint? mode = null;
            if (stat.Mode != uint.MaxValue)
            {
                if (((stat.Mode ^ d.Mode) & Directory) != 0)
                {
                    throw Refused("wstat -- attempt to change directory");
                }

                if ((stat.Mode & ~Settable) != 0)
                {
                    throw Refused("wstat -- unknown bits in qid.type/mode");
                }

                mode = stat.Mode == d.Mode ? null : stat.Mode;
            }

            long? mtime = stat.ModificationTime == uint.MaxValue || stat.ModificationTime * 1_000_000_000L == d.Mtime ? null : stat.ModificationTime * 1_000_000_000L;
            string? owner = stat.User.Length == 0 || stat.User == d.User ? null : stat.User;
            string? group = stat.Group.Length == 0 || stat.Group == d.Group ? null : stat.Group;
            if (stat.LastModifier.Length != 0)
            {
                throw Refused("wstat -- attempt to change muid");
            }

            if (stat.Name.Length == 0 && stat.Length == ulong.MaxValue && stat.Mode == uint.MaxValue && stat.ModificationTime == uint.MaxValue && stat.User.Length == 0 && stat.Group.Length == 0)
            {
                store.Commit();
                return ValueTask.FromResult(stat.EncodedLength);
            }

            bool permitted = inGroup(user, "adm");
            if (rename && !Allowed(Find(Parent(d.Qid.Path)).Dir, user, Write))
            {
                throw Refused("permission denied");
            }

            if (length is not null && !Allowed(d, user, Write))
            {
                throw Refused("permission denied");
            }

            if ((mode is not null || mtime is not null) && !permitted && user != d.User && !inGroup(user, d.Group))
            {
                throw Refused("wstat -- not owner or group leader");
            }

            if (owner is not null && !permitted)
            {
                throw Refused("wstat -- not owner");
            }

            if (group is not null && !permitted && !(user == d.User && inGroup(user, group)) && !(inGroup(user, d.Group) && inGroup(user, group)))
            {
                throw Refused("wstat -- not in group");
            }

            var change = new WstatChange(length, mode, mtime, null, owner, group, user);
            List<Message> cleared = length is { } l && l < d.Length ? Clear(d.Qid.Path, l, d.Length) : [];
            if (rename)
            {
                Dir n = Dir.Read(key, Messages.Apply(d.Value(), new Message(MessageOp.Wstat, key, change.Pack()))) with { Name = stat.Name };
                byte[] renamed = n.Key(Parent(d.Qid.Path));
                cleared.AddRange([
                    new Message(MessageOp.Clobber, key, []),
                    new Message(MessageOp.Insert, renamed, n.Value()),
                    new Message(MessageOp.Insert, Keys.Up(d.Qid.Path), renamed),
                    Touch(Parent(d.Qid.Path))]);
                foreach (Message[] batch in cleared.Chunk(Batch))
                {
                    Upsert(batch);
                }
            }
            else
            {
                Change(key, cleared, change);
            }

            return ValueTask.FromResult(stat.EncodedLength);
        }
    }

    private static long Qid(ResourceHandle handle) => (long)handle.Identity.Path;

    private static bool IsDirectory(Dir d) => (d.Mode & Directory) != 0;

    private static uint Seconds(long nanoseconds) => (uint)(nanoseconds / 1_000_000_000);

    private static string HandleId(ResourceOperationContext context) => $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";

    private static ResourceWStatRejectedException Refused(string error) => new(error);

    // mode2bits
    private static int Bits(byte mode)
    {
        int bits = (mode & 3) switch
        {
            NinePConstants.OREAD => Read,
            NinePConstants.OWRITE => Write,
            NinePConstants.ORDWR => Read | Write,
            _ => Read | Exec,
        };
        return (mode & NinePConstants.OTRUNC) != 0 ? bits | Write : bits;
    }

    // okname
    private static string? NameError(string name)
        => name.Length == 0 || name is "." or ".." || name.Any(c => c < ' ' || c == '/') ? "create/wstat -- bad character in file name"
            : System.Text.Encoding.UTF8.GetByteCount(name) > Format.MaxName ? "name too long"
            : null;

    // fsaccess: the owner's bits, then the group's, then everyone's; none gets only everyone's.
    private bool Allowed(Dir d, string user, int bits)
    {
        if (user != "none" && ((user == d.User && ((d.Mode >> 6) & bits) == bits) || (inGroup(user, d.Group) && ((d.Mode >> 3) & bits) == bits)))
        {
            return true;
        }

        return (d.Mode & bits) == bits;
    }

    private ResourceHandle Handle(Dir d) => new(new ResourceIdentity("gefs", device, (ulong)d.Qid.Path), (QidType)d.Qid.Type, d.Qid.Version);

    // An entry by its qid, through the key its Kup holds.
    private (byte[] Key, Dir Dir) Find(long qid)
    {
        if (tree.Lookup(Keys.Up(qid)) is not { } key || tree.Lookup(key) is not { } value)
        {
            throw new IOException("phase error -- use after remove");
        }

        return (key, Dir.Read(key, value));
    }

    private long Parent(long qid)
    {
        Keys.ReadEntry(tree.Lookup(Keys.Up(qid)) ?? throw new IOException("phase error -- use after remove"), out long parent);
        return parent;
    }

    // touch: a wstat changing nothing, which bumps a directory's version.
    private Message Touch(long qid) => new(MessageOp.Wstat, Find(qid).Key, [0]);

    private Dir Create(long parent, string name, uint permissions, string user)
    {
        if (NameError(name) is { } error)
        {
            throw new IOException(error);
        }

        if ((permissions & (MountPoint | Authentication)) != 0)
        {
            throw new IOException("protocol botch");
        }

        if ((permissions & ~Settable) != 0)
        {
            throw new IOException("wstat -- unknown bits in qid.type/mode");
        }

        var (_, dir) = Find(parent);
        if (tree.Lookup(Keys.Entry(parent, name)) is not null)
        {
            throw new IOException("create/wstat -- file exists");
        }

        if (!IsDirectory(dir))
        {
            throw new IOException("create -- in a non-directory");
        }

        if (!Allowed(dir, user, Write))
        {
            throw new IOException("permission denied");
        }

        uint mode = (permissions & Directory) != 0
            ? permissions & (~0b111_111_111u | (dir.Mode & 0b111_111_111))
            : permissions & (~0b110_110_110u | (dir.Mode & 0b110_110_110));
        long now = Now;
        var d = new Dir(name, new Qid(store.NewQid(), 0, (byte)(mode >> 24)), mode, now, now, 0, user, dir.Group, user);
        byte[] key = d.Key(parent);
        Upsert(new Message(MessageOp.Insert, key, d.Value()), new Message(MessageOp.Insert, Keys.Up(d.Qid.Path), key), Touch(parent));
        return d;
    }

    // candelete and fsremove's permission check.
    private void CanRemove(Dir d, string user)
    {
        if (IsDirectory(d) && HasChildren(d.Qid.Path))
        {
            throw new IOException("directory is not empty");
        }

        long parent = Parent(d.Qid.Path);
        if (parent == -1 || !Allowed(Find(parent).Dir, user, Write))
        {
            throw new IOException("permission denied");
        }
    }

    private bool HasChildren(long qid)
    {
        var prefix = new byte[9];
        prefix[0] = (byte)KeyType.Entry;
        BinaryPrimitives.WriteInt64BigEndian(prefix.AsSpan(1), qid);
        var scan = new Scan(prefix);
        scan.Enter(tree);
        return scan.Next();
    }

    private void Remove((byte[] Key, Dir Dir) found)
    {
        long qid = found.Dir.Qid.Path;
        long parent = Parent(qid);
        var changes = Clear(qid, 0, found.Dir.Length);
        changes.Add(new Message(MessageOp.Delete, found.Key, []));
        changes.Add(new Message(MessageOp.Delete, Keys.Up(qid), []));
        changes.Add(Touch(parent));
        foreach (Message[] batch in changes.Chunk(Batch))
        {
            Upsert(batch);
        }
    }

    // The data blocks from a length to the end of the file cleared; a block the new length ends in is
    // written anew with zeros past it, so lengthening the file again reads zeros there.
    private List<Message> Clear(long qid, long length, long end)
    {
        var changes = new List<Message>();
        long first = length + ((B - (length % B)) % B);
        for (long fb = first; fb < end; fb += B)
        {
            changes.Add(new Message(MessageOp.ClearBlock, Keys.Data(qid, fb), []));
        }

        if (length % B != 0 && tree.Lookup(Keys.Data(qid, length - (length % B))) is { } old)
        {
            Blk b = store.Allocator.New(BlockType.Data, tree.Gen);
            store.Allocator.ReadData(Bptr.Read(old)).AsSpan(0, (int)(length % B)).CopyTo(b.Buffer);
            store.Allocator.Enqueue(b);
            var bp = new byte[Format.PointerSize];
            b.Pointer.Write(bp);
            changes.Add(new Message(MessageOp.Insert, Keys.Data(qid, length - (length % B)), bp));
        }

        return changes;
    }

    // Block changes in batches, then the entry's stat change.
    private void Change(byte[] key, List<Message> changes, WstatChange stat)
    {
        foreach (Message[] batch in changes.Chunk(Batch))
        {
            Upsert(batch);
        }

        Upsert(new Message(MessageOp.Wstat, key, stat.Pack()));
    }

    private void Upsert(params Message[] messages) => tree.Upsert(messages);
}
