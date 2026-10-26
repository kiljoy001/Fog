using System.Buffers.Binary;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Gefs;

// fs.c: a tree a label names, served as files. A directory entry lives under its parent's qid and
// its name; unlike gefs, every entry also keeps the key of its directory entry under its own qid,
// so a handle alone finds it. A file's data lives in blocks under its qid and offset.
public sealed class GefsFs : IResourceDataOperations, IResourceWStatOperations
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

    private readonly Store store;
    private readonly Tree tree;
    private readonly string device;
    private readonly string owner;
    private readonly Func<string, string, bool> inGroup;
    private readonly TimeProvider clock;
    private readonly HashSet<long> exclusive = [];

    // inGroup says whether a user is in a group; every user is in the group of its own name.
    internal GefsFs(Store store, string label, string device, Func<string, string, bool> inGroup, TimeProvider clock)
    {
        this.store = store;
        this.device = device;
        this.inGroup = inGroup;
        this.clock = clock;
        tree = store.Mount(label);
        owner = Find(tree, 0).Dir.User;
    }

    public ResourceHandle Root => Reading(view => Handle(Find(view, 0).Dir));

    private long Now => Store.Nanoseconds(clock);

    // A snapshot's tree, or a store whose commit failed, takes no changes.
    private bool ReadOnly => tree.ReadOnly || store.Broken;

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
        => ValueTask.FromResult(Reading(view =>
        {
            long qid = Qid(directory);
            if (name == "..")
            {
                long parent = Parent(view, qid);
                return Handle(Find(view, parent == -1 ? qid : parent).Dir);
            }

            return view.Lookup(Keys.Entry(qid, name)) is { } value ? Handle(Dir.Read(Keys.Entry(qid, name), value)) : null;
        }));

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ResourceDirectoryEntry>>(Reading(view =>
        {
            Scan scan = Under(view, KeyType.Entry, Qid(directory));
            var entries = new List<ResourceDirectoryEntry>();
            while (scan.Next())
            {
                Dir d = Dir.Read(scan.Key, scan.Value);
                entries.Add(new ResourceDirectoryEntry(d.Name, Handle(d)));
            }

            return entries;
        }));

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => new(store.Writer.Run(() => Handle(Create(Qid(directory), name, directoryEntry ? Directory | 0b111_111_111 : 0b110_110_110, owner))));

    // fscreate: refused as gefs refuses, then an entry owned by its creator in the directory's group.
    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => new(store.Writer.Run(() =>
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
                lock (exclusive)
                {
                    exclusive.Add(d.Qid.Path);
                }
            }

            return new ResourceOpenHandle(Handle(d), HandleId(context), mode, 0);
        }));

    // fsopen: a directory opens only to read; removing on close needs what removing needs; opening
    // with truncation empties the file. An exclusive file opens once at a time. As in gefs, an open
    // that truncates or removes on close is a change, and the rest are reads.
    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if ((mode & (NinePConstants.OTRUNC | NinePConstants.ORCLOSE)) == 0)
        {
            return ValueTask.FromResult(Reading(view => Open(view, resource, mode, context)));
        }

        return new(store.Writer.Run(() => Open(tree, resource, mode, context)));
    }

    // readfile: up to the file's end, holes as zeros.
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => ValueTask.FromResult<ReadOnlyMemory<byte>>(Reading(view =>
        {
            Dir d = Find(view, Qid(openHandle.Resource)).Dir;
            var reply = new byte[(int)Math.Min(count, (ulong)d.Length - Math.Min(offset, (ulong)d.Length))];
            for (int done = 0; done < reply.Length;)
            {
                long at = (long)offset + done;
                int within = (int)(at % B);
                int n = Math.Min((int)B - within, reply.Length - done);
                if (view.Lookup(Keys.Data(d.Qid.Path, at - within)) is { } bp)
                {
                    store.Allocator.ReadData(Bptr.Read(bp)).AsSpan(within, n).CopyTo(reply.AsSpan(done));
                }

                done += n;
            }

            return reply;
        }));

    // fswrite and writeb: each block touched written anew, keeping the bytes around the write from the
    // block it replaces; then the length, modification time and last modifier.
    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        => new(store.Writer.Run(() =>
        {
            if ((Bits(openHandle.Mode) & Write) == 0)
            {
                throw new IOException("resource in use");
            }

            Writable();
            var (key, d) = Find(tree, Qid(openHandle.Resource));
            long start = (d.Mode & Append) != 0 ? d.Length : (long)offset;
            var changes = new List<Message>();
            for (int done = 0; done < data.Length;)
            {
                long at = start + done;
                long fb = at - (at % B);
                int within = (int)(at - fb);
                int n = Math.Min((int)B - within, data.Length - done);
                Blk b = store.Allocator.New(BlockType.Data, tree.Gen);
                if (n != B && tree.Lookup(Keys.Data(d.Qid.Path, fb)) is { } old)
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
            Change(key, changes, new WstatChange(Length: Math.Max(end, d.Length), Mtime: Now, Muid: context.User));
            return (uint)data.Length;
        }));

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
        => ValueTask.FromResult(Reading(view =>
        {
            Dir d = Find(view, Qid(resource)).Dir;
            return new ResourceStat(Handle(d), d.Name.Length == 0 ? "/" : d.Name, d.Mode, Seconds(d.Atime), Seconds(d.Mtime), (ulong)d.Length, d.User, d.Group, d.Muid);
        }));

    // A file opened to be removed on close goes now; it was checked when it was opened.
    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        long qid = Qid(openHandle.Resource);
        lock (exclusive)
        {
            exclusive.Remove(qid);
        }

        return (openHandle.Mode & NinePConstants.ORCLOSE) != 0 ? new(store.Writer.Run(() => Remove(Find(tree, qid)))) : ValueTask.CompletedTask;
    }

    // fsremove: an empty directory or a file, by a user who may write its directory; not the root.
    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => new(store.Writer.Run(() =>
        {
            var found = Find(tree, Qid(resource));
            CanRemove(found.Dir, context.User);
            Remove(found);
        }));

    public ValueTask<uint> WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => WStatAsync(handle.Resource, stat, context, cancellationToken);

    // fswstat: every field checked, then every permission, before anything changes. A wstat that
    // names nothing asks for a sync, and commits the store once the writer has run it.
    public async ValueTask<uint> WStatAsync(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (await store.Writer.Run(() => WStat(resource, stat, context)) && !store.Broken)
        {
            store.Commit();
        }

        return stat.EncodedLength;
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

    private static bool TooLong(string name) => System.Text.Encoding.UTF8.GetByteCount(name) > Format.MaxName;

    private static bool Grants(uint mode, int bits, int shift) => (mode & ((uint)bits << shift)) == (uint)bits << shift;

    // okname
    private static string? NameError(string name)
        => name.Length == 0 || name is "." or ".." || name.Any(c => c < ' ' || c == '/') ? "create/wstat -- bad character in file name"
            : TooLong(name) ? "name too long"
            : null;

    // An entry by its qid, through the key its Kup holds.
    private static (byte[] Key, Dir Dir) Find(Tree t, long qid)
    {
        if (t.Lookup(Keys.Up(qid)) is not { } key || t.Lookup(key) is not { } value)
        {
            throw new IOException("phase error -- use after remove");
        }

        return (key, Dir.Read(key, value));
    }

    private static long Parent(Tree t, long qid)
    {
        Keys.ReadEntry(Find(t, qid).Key, out long parent);
        return parent;
    }

    private static bool HasChildren(Tree t, long qid)
    {
        return Under(t, KeyType.Entry, qid).Next();
    }

    // The keys of one type under a qid: a directory's entries or a file's data blocks.
    private static Scan Under(Tree t, KeyType type, long qid)
    {
        var prefix = new byte[9];
        prefix[0] = (byte)type;
        BinaryPrimitives.WriteInt64BigEndian(prefix.AsSpan(1), qid);
        var scan = new Scan(prefix);
        scan.Enter(t);
        return scan;
    }

    // A wstat may not change the qid, type or device.
    private static void WstatQid(ResourceWStat stat)
    {
        if (stat.Qid.Path != ulong.MaxValue || stat.Qid.Version != uint.MaxValue || stat.Qid.Type != (QidType)0xff || stat.Type != ushort.MaxValue || stat.Device != uint.MaxValue)
        {
            throw Refused("wstat -- attempt to change qid");
        }
    }

    // The modification time the wstat gives, in nanoseconds, if another.
    private static long? WstatMtime(ResourceWStat stat, Dir d)
        => stat.ModificationTime == uint.MaxValue || stat.ModificationTime * 1_000_000_000L == d.Mtime ? null : stat.ModificationTime * 1_000_000_000L;

    // Whether the wstat asks for nothing, which is a sync.
    private static bool Unasked(ResourceWStat stat)
        => stat.Name.Length == 0 && stat.Length == ulong.MaxValue && stat.Mode == uint.MaxValue && stat.ModificationTime == uint.MaxValue && stat.User.Length == 0 && stat.Group.Length == 0;

    // The length the wstat gives a file, if another; a directory has none to give.
    private static long? WstatLength(ResourceWStat stat, Dir d)
    {
        if (stat.Length == ulong.MaxValue)
        {
            return null;
        }

        if (IsDirectory(d) || stat.Length > long.MaxValue)
        {
            throw Refused("wstat -- attempt to make length negative");
        }

        return stat.Length == (ulong)d.Length ? null : (long)stat.Length;
    }

    // The mode the wstat gives, if another, which may not make or unmake a directory.
    private static uint? WstatMode(ResourceWStat stat, Dir d)
    {
        if (stat.Mode == uint.MaxValue)
        {
            return null;
        }

        if (((stat.Mode ^ d.Mode) & Directory) != 0)
        {
            throw Refused("wstat -- attempt to change directory");
        }

        if ((stat.Mode & ~Settable) != 0)
        {
            throw Refused("wstat -- unknown bits in qid.type/mode");
        }

        return stat.Mode == d.Mode ? null : stat.Mode;
    }

    // The owner and group the wstat gives, if others; the muid is not to be given.
    private static (string? Owner, string? Group) WstatOwners(ResourceWStat stat, Dir d)
    {
        if (TooLong(stat.User) || TooLong(stat.Group))
        {
            throw Refused("name too long");
        }

        string? owner = stat.User.Length == 0 || stat.User == d.User ? null : stat.User;
        string? group = stat.Group.Length == 0 || stat.Group == d.Group ? null : stat.Group;
        if (stat.LastModifier.Length != 0)
        {
            throw Refused("wstat -- attempt to change muid");
        }

        return (owner, group);
    }

    // The wstat's checks and changes, under the writer's lock; whether it named nothing.
    private bool WStat(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context)
    {
        var (key, d) = Find(tree, Qid(resource));
        string user = context.User;
        WstatQid(stat);
        bool rename = WstatRename(stat, d);
        long? length = WstatLength(stat, d);
        uint? mode = WstatMode(stat, d);
        long? mtime = WstatMtime(stat, d);
        (string? owner, string? group) = WstatOwners(stat, d);
        if (Unasked(stat))
        {
            return true;
        }

        WstatPermitted(d, user, rename, length is not null, mode is not null || mtime is not null, owner is not null, group);
        if (ReadOnly)
        {
            throw Refused("file system read only");
        }

        Wstat(key, d, rename ? stat.Name : null, new WstatChange(length, mode, mtime, null, owner, group, user));
        return false;
    }

    // Whether the wstat renames the file, to a good name not already in its directory.
    private bool WstatRename(ResourceWStat stat, Dir d)
    {
        bool rename = stat.Name.Length != 0 && stat.Name != d.Name;
        if (rename)
        {
            if (NameError(stat.Name) is { } error)
            {
                throw Refused(error);
            }

            if (tree.Lookup(Keys.Entry(Parent(tree, d.Qid.Path), stat.Name)) is not null)
            {
                throw Refused("create/wstat -- file exists");
            }
        }

        return rename;
    }

    // Whether the user may make the changes: a rename needs the directory's write, a length the
    // file's, a mode or time its owner or group leader, an owner adm, and a group membership.
    private void WstatPermitted(Dir d, string user, bool rename, bool length, bool modeOrTime, bool owner, string? group)
    {
        bool permitted = inGroup(user, "adm");
        if (rename && !Allowed(Find(tree, Parent(tree, d.Qid.Path)).Dir, user, Write))
        {
            throw Refused("permission denied");
        }

        if (length && !Allowed(d, user, Write))
        {
            throw Refused("permission denied");
        }

        if (modeOrTime && !permitted && user != d.User && !inGroup(user, d.Group))
        {
            throw Refused("wstat -- not owner or group leader");
        }

        if (owner && !permitted)
        {
            throw Refused("wstat -- not owner");
        }

        if (group is not null && !permitted && !MayRegroup(d, user, group))
        {
            throw Refused("wstat -- not in group");
        }
    }

    // A group the user may give the file: one the user is in, as its owner or a member of its group.
    private bool MayRegroup(Dir d, string user, string group)
        => inGroup(user, group) && (user == d.User || inGroup(user, d.Group));

    // The change made, and the file renamed to the name given, if one is.
    private void Wstat(byte[] key, Dir d, string? name, WstatChange change)
    {
        List<Message> cleared = change.Length is { } l ? Clear(d.Qid.Path, l) : [];
        if (name is null)
        {
            Change(key, cleared, change);
            return;
        }

        Dir n = Dir.Read(key, Messages.Apply(d.Value(), new Message(MessageOp.Wstat, key, change.Pack()))) with { Name = name };
        byte[] renamed = n.Key(Parent(tree, d.Qid.Path));
        cleared.AddRange([
            new Message(MessageOp.Clobber, key, []),
            new Message(MessageOp.Insert, renamed, n.Value()),
            new Message(MessageOp.Insert, Keys.Up(d.Qid.Path), renamed),
            Touch(Parent(tree, d.Qid.Path))]);
        foreach (Message[] batch in cleared.Chunk(Batch))
        {
            Upsert(batch);
        }
    }

    // fsopen's checks: an exclusive file locked, then a directory opened to write, removing on close,
    // and the permissions asked for. An exclusive file is claimed before the checks, and given back if
    // one fails.
    private ResourceOpenHandle Open(Tree t, ResourceHandle resource, byte mode, ResourceOperationContext context)
    {
        var (key, d) = Find(t, Qid(resource));
        bool claimed = (d.Qid.Type & (byte)QidType.QTEXCL) != 0;
        if (claimed)
        {
            lock (exclusive)
            {
                if (!exclusive.Add(d.Qid.Path))
                {
                    throw new IOException("open/create -- file is locked");
                }
            }
        }

        try
        {
            int bits = Bits(mode);
            if (IsDirectory(d) && (bits & Write) != 0)
            {
                throw new IOException("permission denied");
            }

            if ((mode & NinePConstants.ORCLOSE) != 0)
            {
                CanRemove(d, context.User);
            }

            if (!Allowed(d, context.User, bits))
            {
                throw new IOException("permission denied");
            }

            if ((mode & NinePConstants.OTRUNC) != 0 && (d.Mode & Append) == 0)
            {
                Writable();
                Change(key, Clear(d.Qid.Path, 0), new WstatChange(Length: 0, Muid: context.User));
                d = Find(t, d.Qid.Path).Dir;
            }

            return new ResourceOpenHandle(Handle(d), HandleId(context), mode, 0);
        }
        catch when (claimed)
        {
            lock (exclusive)
            {
                exclusive.Remove(d.Qid.Path);
            }

            throw;
        }
    }

    // A read on the tree as it is when the read starts, inside an epoch so the blocks it uses are not
    // reused until it is done.
    private T Reading<T>(Func<Tree, T> read)
    {
        int epoch = store.Allocator.Epochs.Start();
        try
        {
            return read(tree.Pinned());
        }
        finally
        {
            store.Allocator.Epochs.End(epoch);
        }
    }

    // fsaccess: the owner's bits, then the group's, then everyone's; none gets only everyone's.
    private bool Allowed(Dir d, string user, int bits)
    {
        if (user != "none" && ((user == d.User && Grants(d.Mode, bits, 6)) || (inGroup(user, d.Group) && Grants(d.Mode, bits, 3))))
        {
            return true;
        }

        return Grants(d.Mode, bits, 0);
    }

    private void Writable()
    {
        if (ReadOnly)
        {
            throw new IOException("file system read only");
        }
    }

    private ResourceHandle Handle(Dir d) => new(new ResourceIdentity("gefs", device, (ulong)d.Qid.Path), (QidType)d.Qid.Type, d.Qid.Version);

    // touch: a wstat changing nothing, which bumps a directory's version.
    private Message Touch(long qid) => new(MessageOp.Wstat, Find(tree, qid).Key, [0]);

    private Dir Create(long parent, string name, uint permissions, string user)
    {
        Writable();
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

        var (_, dir) = Find(tree, parent);
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
        var d = new Dir(name, new Qid(store.NewQid(), 0, (byte)(mode / (1u << 24))), mode, now, now, 0, user, dir.Group, user);
        byte[] key = d.Key(parent);
        Upsert(new Message(MessageOp.Insert, key, d.Value()), new Message(MessageOp.Insert, Keys.Up(d.Qid.Path), key), Touch(parent));
        return d;
    }

    // candelete and fsremove's permission check.
    private void CanRemove(Dir d, string user)
    {
        if (IsDirectory(d) && HasChildren(tree, d.Qid.Path))
        {
            throw new IOException("directory is not empty");
        }

        long parent = Parent(tree, d.Qid.Path);
        if (parent == -1 || !Allowed(Find(tree, parent).Dir, user, Write))
        {
            throw new IOException("permission denied");
        }
    }

    private void Remove((byte[] Key, Dir Dir) found)
    {
        Writable();
        long qid = found.Dir.Qid.Path;
        long parent = Parent(tree, qid);
        var changes = Clear(qid, 0);
        changes.Add(new Message(MessageOp.Delete, found.Key, []));
        changes.Add(new Message(MessageOp.Delete, Keys.Up(qid), []));
        changes.Add(Touch(parent));
        foreach (Message[] batch in changes.Chunk(Batch))
        {
            Upsert(batch);
        }
    }

    // The data blocks past a length cleared; a block the length ends in is written anew with zeros
    // past it, so lengthening the file again reads zeros there.
    private List<Message> Clear(long qid, long length)
    {
        var changes = new List<Message>();
        long last = length - (length % B);
        Scan scan = Under(tree, KeyType.Data, qid);
        while (scan.Next())
        {
            if (BinaryPrimitives.ReadInt64BigEndian(scan.Key.AsSpan(9)) is var fb && fb >= last)
            {
                changes.Add(fb == last && length != last ? Truncated(scan.Key, scan.Value, (int)(length - last)) : new Message(MessageOp.ClearBlock, scan.Key, []));
            }
        }

        return changes;
    }

    // A data block written anew with only its first bytes.
    private Message Truncated(byte[] key, byte[] value, int keep)
    {
        Blk b = store.Allocator.New(BlockType.Data, tree.Gen);
        store.Allocator.ReadData(Bptr.Read(value)).AsSpan(0, keep).CopyTo(b.Buffer);
        store.Allocator.Enqueue(b);
        var bp = new byte[Format.PointerSize];
        b.Pointer.Write(bp);
        return new Message(MessageOp.Insert, key, bp);
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
