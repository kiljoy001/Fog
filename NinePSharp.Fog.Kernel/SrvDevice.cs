using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devsrv: #s, files that hold open channels, and boards made by opening clone that hold more. A
// walk carries no user, so walking into another's board is not checked as srvgen checks it; opens,
// creates and removes are.
internal sealed class SrvDevice(FogKernel kernel) : IResourceDataOperations, IResourceWStatOperations
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
    private readonly Board root = new() { Name = "#s", Perm = 0b111_111_111, Owner = kernel.User };
    private ulong srvpath = 1;

    public static ResourceHandle Root { get; } = Handle(0, Qsrv, QidType.QTDIR);

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (Encoding.UTF8.GetByteCount(name) >= GenbufSize || BoardOf(directory) is not { } b)
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

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return ValueTask.FromResult(Type(resource) switch
            {
                Qclone or Qlease => new ResourceStat(resource, Type(resource) == Qclone ? Clone : BoardOf(resource)!.Name, 0b100_100_100, 0, 0, 0, kernel.User, kernel.User, kernel.User),
                _ when resource.IsDirectory && BoardOf(resource) is { } b => new ResourceStat(resource, b.Name, b.Perm | (uint)NinePConstants.FileMode9P.DMDIR, 0, 0, 0, b.Owner, b.Owner, b.Owner),
                _ when srvs.TryGetValue(SrvPath(resource), out var posted) => new ResourceStat(resource, posted.Srv.Name, posted.Srv.Perm, 0, 0, 0, posted.Srv.Owner, posted.Srv.Owner, posted.Srv.Owner),
                _ => throw new IOException(Errors.Shutdown),
            });
        }
    }

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
            if (Type(resource) == Qclone)
            {
                Board b = BoardOf(resource) ?? throw new IOException(Errors.Shutdown);
                if (b.Closed)
                {
                    throw new IOException(Errors.Shutdown);
                }

                var ch = new Board { Perm = 0b111_111_000, Owner = context.User, Parent = b };
                do
                {
                    ch.Path = srvpath++;
                    ch.Name = ch.Path.ToString(CultureInfo.InvariantCulture);
                }
                while (b.Srv.Exists(sp => sp.Name == ch.Name));

                b.Child.Insert(0, ch);
                boards[ch.Path] = ch;
                return ValueTask.FromResult(Opened(Handle(ch.Path, Qlease, QidType.QTFILE), mode, context));
            }

            if (resource.IsDirectory)
            {
                Board b = BoardOf(resource) ?? throw new IOException(Errors.Shutdown);
                if (mode != NinePConstants.OREAD)
                {
                    throw new IOException(Errors.IsDirectory);
                }

                PermCheck(context, b.Owner, b.Perm, mode);
                return ValueTask.FromResult(Opened(resource, mode, context));
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

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new NotSupportedException("#s creates only by opening what it creates");

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
            return ValueTask.FromResult(Opened(Handle(sp.Path, Qsrv, QidType.QTFILE), mode, context));
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        if (Type(openHandle.Resource) != Qlease)
        {
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        lock (gate)
        {
            byte[] name = Encoding.UTF8.GetBytes(BoardOf(openHandle.Resource)!.Name);
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
            error = !srvs.TryGetValue(SrvPath(openHandle.Resource), out var posted) ? Errors.NotExist
                : posted.Board.Closed ? Errors.Shutdown
                : posted.Srv.Chan is not null ? Errors.BadUseFd
                : null;
            if (error is null)
            {
                posted.Srv.Chan = c1;
            }
        }

        if (error is not null)
        {
            await c1.DisposeAsync();
            throw new IOException(error);
        }

        return (uint)data.Length;
    }

    // srvclose: a lease's close closes its board and what is posted in it; a file created with
    // ORCLOSE is removed.
    public async ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (Type(openHandle.Resource) == Qlease)
        {
            List<Srv> released;
            lock (gate)
            {
                Board b = BoardOf(openHandle.Resource)!;
                released = [.. b.Srv];
                b.Srv.Clear();
                b.Closed = true;
                foreach (Srv sp in released)
                {
                    srvs.Remove(sp.Path);
                }

                // A board is left while it has children; a closed leaf takes its closed parents with it.
                for (; b.Closed && b.Child.Count == 0 && b.Parent is { } parent; b = parent)
                {
                    parent.Child.Remove(b);
                    boards.Remove(b.Path);
                }
            }

            foreach (Srv sp in released)
            {
                await ReleaseAsync(sp);
            }

            return;
        }

        if ((openHandle.Mode & NinePConstants.ORCLOSE) != 0)
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

    // srvwstat: a name, owner and mode, by the owner or the host owner.
    public ValueTask<uint> WStatAsync(ResourceHandle resource, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (Type(resource) != Qsrv || (resource.IsDirectory && SrvPath(resource) == 0))
        {
            throw new ResourceWStatRejectedException(Errors.Permission);
        }

        lock (gate)
        {
            Link lp;
            Board scope;
            if (resource.IsDirectory)
            {
                Board b = BoardOf(resource) ?? throw new ResourceWStatRejectedException(Errors.NotExist);
                (lp, scope) = (b, b.Parent!);
                if (b.Closed)
                {
                    throw new ResourceWStatRejectedException(Errors.Shutdown);
                }
            }
            else
            {
                if (!srvs.TryGetValue(SrvPath(resource), out var posted))
                {
                    throw new ResourceWStatRejectedException(Errors.NotExist);
                }

                (lp, scope) = posted;
                if (posted.Board.Closed)
                {
                    throw new ResourceWStatRejectedException(Errors.Shutdown);
                }
            }

            if (lp.Owner != context.User && context.User != kernel.User)
            {
                throw new ResourceWStatRejectedException(Errors.Permission);
            }

            if (stat.Name.Length != 0 && stat.Name != lp.Name)
            {
                if (stat.Name.Contains('/', StringComparison.Ordinal))
                {
                    throw new ResourceWStatRejectedException(Errors.BadChar);
                }

                if (Encoding.UTF8.GetByteCount(stat.Name) >= GenbufSize)
                {
                    throw new ResourceWStatRejectedException(Errors.TooLong);
                }

                if (stat.Name == Clone || scope.Srv.Exists(sp => sp.Name == stat.Name) || scope.Child.Exists(ch => ch.Name == stat.Name))
                {
                    throw new ResourceWStatRejectedException(Errors.Exist);
                }

                lp.Name = stat.Name;
            }

            if (stat.User.Length != 0)
            {
                lp.Owner = stat.User;
            }

            if (stat.Mode != uint.MaxValue)
            {
                lp.Perm = stat.Mode & 0b111_111_111;
            }

            return ValueTask.FromResult(stat.EncodedLength);
        }
    }

    public ValueTask<uint> WStatOpenAsync(ResourceOpenHandle handle, ResourceWStat stat, ResourceOperationContext context, CancellationToken cancellationToken)
        => WStatAsync(handle.Resource, stat, context, cancellationToken);

    private static ResourceHandle Handle(ulong path, int type, QidType qid) => new(new ResourceIdentity(Provider, "#s", (path << 2) | (uint)type), qid);

    private static int Type(ResourceHandle resource) => (int)(resource.Identity.Path & 3);

    private static ulong SrvPath(ResourceHandle resource) => resource.Identity.Path >> 2;

    private static async ValueTask ReleaseAsync(Srv sp)
    {
        if (sp.Chan is { } chan)
        {
            await chan.DisposeAsync();
        }
    }

    private static ResourceOpenHandle Opened(ResourceHandle resource, byte mode, ResourceOperationContext context)
        => new(resource, $"{context.OperationId.SessionId}/{context.OperationId.Sequence}", mode, 0);

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
        public string Name { get; set; } = string.Empty;

        public string Owner { get; set; } = string.Empty;

        public uint Perm { get; set; }

        public ulong Path { get; set; }
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
    }
}
