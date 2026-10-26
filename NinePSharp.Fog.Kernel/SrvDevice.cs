using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devsrv: #s, files that hold open channels, and boards made by opening clone that hold more. A
// walk carries no user, so walking into another's board is not checked as srvgen checks it; opens,
// creates and removes are. A board is held, as boardclunk counts, by its lease and by each open of
// it or of a file made in it, which is the board's channel's aux; a walk alone, as a bind or a
// current directory keeps, does not hold it.
internal sealed class SrvDevice(FogKernel kernel) : IResourceDataOperations, IResourceWStatOperations, IDirectoryOpenRefusal
{
    public const string Provider = "srv";
    private const string Clone = "clone";
    private const int Qsrv = 0;
    private const int Qclone = 1;
    private const int Qlease = 2;
    private const int GenbufSize = 128;
    private static readonly uint[] Access = [0b100_000_000, 0b010_000_000, 0b110_000_000, 0b001_000_000];
    private readonly object gate = new();
    private readonly Dictionary<ulong, Board> boards = [];
    private readonly Dictionary<ulong, (Srv Srv, Board Board)> srvs = [];
    private readonly Dictionary<string, Board> auxes = [];

    // Stryker disable once String : the root stats under the name it was reached by, as srvgen's dot is named by the channel's path
    private readonly Board root = new() { Name = "#s", Perm = 0b111_111_111, Owner = kernel.User };
    private ulong srvpath = 1;

    public static ResourceHandle Root { get; } = Handle(0, Qsrv, QidType.QTDIR);

    // srvgen by name: clone, a posted file, then a board; no name as long as genbuf is ever made.
    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (BoardOf(directory) is not { } b)
            {
                return ValueTask.FromResult<ResourceHandle?>(null);
            }

            ResourceHandle? found = name == Clone
                ? Handle(b.Path, Qclone, QidType.QTFILE)
                : b.Srv.Find(sp => sp.Name == name) is { } sp
                    ? Handle(sp.Path, Qsrv, QidType.QTFILE)
                    : b.Child.Find(ch => ch.Name == name) is { } ch
                        ? Handle(ch.Path, Qsrv, QidType.QTDIR)
                        : null;
            return ValueTask.FromResult(found);
        }
    }

    // srvrenameuser: each board and file of the old user becomes the new user's.
    public void Renameuser(string old, string @new)
    {
        void Rename(Link l) => l.Owner = l.Owner == old ? @new : l.Owner;
        void RenameBoard(Board b)
        {
            Rename(b);
            foreach (Srv sp in b.Srv)
            {
                Rename(sp);
            }
        }

        lock (gate)
        {
            RenameBoard(root);
            foreach (Board b in boards.Values)
            {
                RenameBoard(b);
            }
        }
    }

    // A board reached by a walk alone that has gone since is shut down.
    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Board b = BoardOf(directory) ?? throw new IOException(Errors.Shutdown);
            IReadOnlyList<ResourceDirectoryEntry> entries =
            [
                new(Clone, Handle(b.Path, Qclone, QidType.QTFILE)),
                .. b.Srv.Select(sp => new ResourceDirectoryEntry(sp.Name, Handle(sp.Path, Qsrv, QidType.QTFILE))),
                .. b.Child.Select(ch => new ResourceDirectoryEntry(ch.Name, Handle(ch.Path, Qsrv, QidType.QTDIR))),
            ];
            return ValueTask.FromResult(entries);
        }
    }

    // srvstat: a board as srvgen gives its dot, and a file as devstat finds it among its board's
    // entries, where a lease, whose qid no entry has, is not found.
    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return ValueTask.FromResult(Type(resource) switch
            {
                Qclone => new ResourceStat(resource, Clone, 0b100_100_100, 0, 0, 0, kernel.User, kernel.User, kernel.User),
                Qsrv when resource.IsDirectory && BoardOf(resource) is { } b => new ResourceStat(resource, b.Name, b.Perm | (uint)NinePConstants.FileMode9P.DMDIR, 0, 0, 0, b.Owner, b.Owner, b.Owner),
                Qsrv when !resource.IsDirectory && srvs.TryGetValue(SrvPath(resource), out var posted) => new ResourceStat(resource, posted.Srv.Name, posted.Srv.Perm, 0, 0, 0, posted.Srv.Owner, posted.Srv.Owner, posted.Srv.Owner),
                _ => throw new IOException(Errors.NotExist),
            });
        }
    }

    // srvopen of a board for more than reading: Eexist for OTRUNC, as for any file, and Eisdir.
    public string Refusal(byte mode) => (mode & NinePConstants.OTRUNC) != 0 ? Errors.Exist : Errors.IsDirectory;

    // srvopen: clone makes a board; a board opens for reading; a posted file gives its channel.
    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if ((mode & NinePConstants.OTRUNC) != 0)
        {
            throw new IOException(Errors.Exist);
        }

        if ((mode & NinePConstants.ORCLOSE) != 0)
        {
            throw new IOException(Errors.Permission);
        }

        lock (gate)
        {
            if (Type(resource) == Qclone || resource.IsDirectory)
            {
                return ValueTask.FromResult(OpenBoard(resource, mode, context, BoardOf(resource) ?? throw new IOException(Errors.Shutdown)));
            }

            if (!srvs.TryGetValue(SrvPath(resource), out var posted) || posted.Board.Closed || posted.Srv.Chan is not { } nc)
            {
                throw new IOException(Errors.Shutdown);
            }

            int open = mode & 3;
            int held = nc.Handle.Mode & 3;
            if (open != held && held != NinePConstants.ORDWR)
            {
                throw new IOException(Errors.Permission);
            }

            PermCheck(context, posted.Srv.Owner, posted.Srv.Perm, mode);
            throw new SrvOpenException(nc);
        }
    }

    // The namespace makes a file in #s only by opening what it makes.
    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    // srvcreate: an empty file, open for writing the number of the descriptor to post.
    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if ((mode & 3) != NinePConstants.OWRITE)
        {
            throw new IOException(Errors.Permission);
        }

        if (Encoding.UTF8.GetByteCount(name) >= GenbufSize)
        {
            throw new IOException(Errors.TooLong);
        }

        if (name == Clone)
        {
            throw new IOException(Errors.Exist);
        }

        lock (gate)
        {
            Board b = BoardOf(directory) ?? throw new IOException(Errors.Shutdown);
            if (b.Closed)
            {
                throw new IOException(Errors.Shutdown);
            }

            PermCheck(context, b.Owner, b.Perm, NinePConstants.OWRITE);
            if (b.Srv.Exists(sp => sp.Name == name) || b.Child.Exists(ch => ch.Name == name))
            {
                throw new IOException(Errors.Exist);
            }

            var sp = new Srv { Name = name, Owner = context.User, Perm = permissions & 0b111_111_111, Path = srvpath++ };
            b.Srv.Insert(0, sp);
            srvs[sp.Path] = (sp, b);
            b.Ref++;
            return ValueTask.FromResult(Opened(Handle(sp.Path, Qsrv, QidType.QTFILE), mode, context, b));
        }
    }

    // srvread: a lease reads its board's name; a board's own reading is its directory's.
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            byte[] name = Encoding.UTF8.GetBytes(auxes[openHandle.HandleId].Name);
            int from = (int)Math.Min(offset, (ulong)name.Length);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(name.AsMemory(from, (int)Math.Min(count, (uint)(name.Length - from))));
        }
    }

    // srvwrite: the channel of the writer's descriptor, by its number, posted in the file.
    public async ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (Type(openHandle.Resource) == Qlease)
        {
            throw new IOException(Errors.Permission);
        }

        if (data.Length >= 32)
        {
            throw new IOException(Errors.TooBig);
        }

        int fd = (int)Libc.Strtoul(Encoding.UTF8.GetString(data.Span), 0, out _);
        DescriptorLease c1;
        try
        {
            c1 = kernel.Table.Get(context.ProcessId).Descriptors.Acquire(fd);
        }
        catch (ArgumentException)
        {
            throw new IOException(Errors.BadFd);
        }

        string? error;
        lock (gate)
        {
            Board b = auxes[openHandle.HandleId];
            Srv? sp = b.Srv.Find(sp => sp.Path == SrvPath(openHandle.Resource));
            error = b.Closed ? Errors.Shutdown
                : sp is null ? Errors.NotExist
                : sp.Chan is not null ? Errors.BadUseFd
                : null;
            if (error is null)
            {
                sp!.Chan = c1;
            }
        }

        if (error is not null)
        {
            await c1.DisposeAsync();
            throw new IOException(error);
        }

        return (uint)data.Length;
    }

    // srvclose: a file created with ORCLOSE is removed; a lease's close closes its board and what
    // is posted in it; and the board is let go.
    public async ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if ((openHandle.Mode & NinePConstants.ORCLOSE) != 0 && Type(openHandle.Resource) != Qlease)
        {
            try
            {
                await RemoveAsync(openHandle.Resource, openHandle, context, cancellationToken);
            }
            catch (IOException)
            {
                // A file that cannot be removed now stays, as srvclose leaves it.
            }
        }

        List<Srv> released = [];
        lock (gate)
        {
            auxes.Remove(openHandle.HandleId, out Board? b);
            if (Type(openHandle.Resource) == Qlease)
            {
                released = [.. b!.Srv];
                b.Srv.Clear();
                b.Closed = true;
                foreach (Srv sp in released)
                {
                    srvs.Remove(sp.Path);
                }
            }

            Boardclunk(b!);
        }

        foreach (Srv sp in released)
        {
            await ReleaseAsync(sp);
        }
    }

    // srvremove: only a posted file, by its owner or the host owner.
    public async ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (resource.IsDirectory || Type(resource) != Qsrv)
        {
            throw new IOException(Errors.Permission);
        }

        Srv sp;
        lock (gate)
        {
            if (!srvs.TryGetValue(SrvPath(resource), out var posted))
            {
                throw new IOException(Errors.NotExist);
            }

            if (posted.Srv.Owner != context.User && context.User != kernel.User)
            {
                throw new IOException(Errors.Permission);
            }

            (sp, Board b) = posted;
            b.Srv.Remove(sp);
            srvs.Remove(sp.Path);
        }

        await ReleaseAsync(sp);
    }

    public ValueTask<uint> WStatAsync(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return ValueTask.FromResult(WStat(resource, resource.IsDirectory ? BoardOf(resource) : srvs.GetValueOrDefault(SrvPath(resource)).Board, stat, context));
        }
    }

    public ValueTask<uint> WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return ValueTask.FromResult(WStat(handle.Resource, auxes.GetValueOrDefault(handle.HandleId), stat, context));
        }
    }

    // A new name, which must fit genbuf and be none of clone, a file or a board where it is.
    private static string Rename(string name, Board scope)
    {
        if (Encoding.UTF8.GetByteCount(name) >= GenbufSize)
        {
            throw new ResourceWStatRejectedException(Errors.TooLong);
        }

        if (name == Clone || scope.Srv.Exists(sp => sp.Name == name) || scope.Child.Exists(ch => ch.Name == name))
        {
            throw new ResourceWStatRejectedException(Errors.Exist);
        }

        return name;
    }

    private static ResourceHandle Handle(ulong path, int type, QidType qid) => new(new ResourceIdentity(Provider, "#s", (path << 2) | (uint)type), qid);

    private static int Type(ResourceHandle resource) => (int)(resource.Identity.Path & 3);

    // Stryker disable once Bitwise : a path is unsigned, so >> already shifts in zeros as >>> does
    private static ulong SrvPath(ResourceHandle resource) => resource.Identity.Path >> 2;

    private static string Number(ulong path) => path.ToString(CultureInfo.InvariantCulture);

    private static async ValueTask ReleaseAsync(Srv sp)
    {
        if (sp.Chan is { } chan)
        {
            await chan.DisposeAsync();
        }
    }

    // srvwstat: a name, owner and mode of a board or a posted file of its board, by the owner or the
    // host owner; not of the root, clone or a lease. The board is the channel's, or the one the
    // file was found in. A name with a slash is refused by validstat before it gets here.
    private uint WStat(ResourceHandle resource, Board? b, ResourceWStat stat, ResourceOperationContext context)
    {
        if (Type(resource) != Qsrv || (resource.IsDirectory && SrvPath(resource) == 0))
        {
            throw new ResourceWStatRejectedException(Errors.Permission);
        }

        if (b is null)
        {
            throw new ResourceWStatRejectedException(Errors.NotExist);
        }

        if (b.Closed)
        {
            throw new ResourceWStatRejectedException(Errors.Shutdown);
        }

        Link lp = resource.IsDirectory ? b : b.Srv.Find(sp => sp.Path == SrvPath(resource)) ?? throw new ResourceWStatRejectedException(Errors.NotExist);
        if (lp.Owner != context.User && context.User != kernel.User)
        {
            throw new ResourceWStatRejectedException(Errors.Permission);
        }

        if (stat.Name.Length != 0 && stat.Name != lp.Name)
        {
            lp.Name = Rename(stat.Name, resource.IsDirectory ? b.Parent! : b);
        }

        if (stat.User.Length != 0)
        {
            lp.Owner = stat.User;
        }

        if (stat.Mode != uint.MaxValue)
        {
            lp.Perm = stat.Mode & 0b111_111_111;
        }

        return stat.EncodedLength;
    }

    // A clone, which makes a board held by its lease, or a board itself, opened for reading.
    private ResourceOpenHandle OpenBoard(ResourceHandle resource, byte mode, ResourceOperationContext context, Board b)
    {
        if (Type(resource) == Qclone)
        {
            if (b.Closed)
            {
                throw new IOException(Errors.Shutdown);
            }

            ulong path;
            do
            {
                path = srvpath++;
            }
            while (b.Srv.Exists(sp => sp.Name == Number(path)));

            var ch = new Board { Name = Number(path), Owner = context.User, Perm = 0b111_111_000, Path = path, Parent = b, Ref = 1 };
            b.Child.Insert(0, ch);
            boards[ch.Path] = ch;
            return Opened(Handle(ch.Path, Qlease, QidType.QTFILE), mode, context, ch);
        }

        PermCheck(context, b.Owner, b.Perm, mode);
        b.Ref++;
        return Opened(resource, mode, context, b);
    }

    private ResourceOpenHandle Opened(ResourceHandle resource, byte mode, ResourceOperationContext context, Board b)
    {
        var opened = new ResourceOpenHandle(resource, $"{context.OperationId.SessionId}/{context.OperationId.Sequence}", mode, 0);
        auxes[opened.HandleId] = b;
        return opened;
    }

    // boardclunk: the root is never let go; another board, once nothing holds it, goes when it is
    // closed and has no children, and takes its closed and childless parents with it.
    private void Boardclunk(Board b)
    {
        if (b == root || --b.Ref > 0)
        {
            return;
        }

        for (; b.Closed && b.Child.Count == 0; b = b.Parent!)
        {
            b.Parent!.Child.Remove(b);
            boards.Remove(b.Path);
        }
    }

    // devpermcheck: the owner's bits for the owner, the group's for the host owner, the others' else.
    private void PermCheck(ResourceOperationContext context, string owner, uint perm, int mode)
    {
        int shift = context.User == owner ? 0 : context.User == kernel.User ? 3 : 6;
        uint access = Access[mode & 3];
        if (((perm << shift) & access) != access)
        {
            throw new IOException(Errors.Permission);
        }
    }

    private Board? BoardOf(ResourceHandle resource) => SrvPath(resource) == 0 ? root : boards.GetValueOrDefault(SrvPath(resource));

    private abstract class Link
    {
        public required string Name { get; set; }

        public required string Owner { get; set; }

        public uint Perm { get; set; }

        public ulong Path { get; init; }
    }

    private sealed class Srv : Link
    {
        public DescriptorLease? Chan { get; set; }
    }

    private sealed class Board : Link
    {
        public List<Srv> Srv { get; } = [];

        public List<Board> Child { get; } = [];

        public Board? Parent { get; init; }

        public bool Closed { get; set; }

        public int Ref { get; set; }
    }
}
