using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devproc: #p, a directory for each process holding its args, fd, note, noteid and notepg. The rest
// of devproc's files, and its trace, are left out.
internal sealed class ProcDevice(FogKernel kernel) : IResourceDataOperations
{
    public const string Provider = "proc";
    private const ulong Slots = 8;
    private const uint Directory = (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101;
    private const uint ProcMode = 0b110_100_000;
    private static readonly int[] Access = [0b100_000_000, 0b010_000_000, 0b110_000_000, 0b001_000_000];

    // procdir's entries, in its order, with their permissions; 0 takes the process's procmode.
    private static readonly (string Name, uint Permissions)[] Files =
    [
        ("args", 0b110_110_000),
        ("fd", 0b100_100_100),
        ("note", 0),
        ("noteid", 0b110_110_100),
        ("notepg", 0),
    ];

    private enum File
    {
        Directory,
        Args,
        Fd,
        Note,
        NoteId,
        NotePg,
    }

    public static ResourceHandle Root { get; } = Handle(0, QidType.QTDIR);

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        ResourceHandle? found = null;
        if (directory.Identity.Path == 0)
        {
            if (long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out long pid) && Format(pid) == name && kernel.Processes.ContainsKey(pid))
            {
                found = Handle((ulong)pid * Slots, QidType.QTDIR);
            }
        }
        else if (Of(directory) == File.Directory && Array.FindIndex(Files, file => file.Name == name) is int index and >= 0)
        {
            found = Handle(directory.Identity.Path | (uint)(index + 1), QidType.QTFILE);
        }

        return ValueTask.FromResult(found);
    }

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
    {
        IReadOnlyList<ResourceDirectoryEntry> entries = directory.Identity.Path == 0
            ? kernel.Processes.Keys.Order().Select(pid => new ResourceDirectoryEntry(Format(pid), Handle((ulong)pid * Slots, QidType.QTDIR))).ToArray()
            : Files.Select((file, index) => new ResourceDirectoryEntry(file.Name, Handle(directory.Identity.Path | (uint)(index + 1), QidType.QTFILE))).ToArray();
        return ValueTask.FromResult(entries);
    }

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        if (resource.Identity.Path == 0)
        {
            return ValueTask.FromResult(new ResourceStat(resource, "#p", Directory, 0, 0, 0, kernel.User, kernel.User, kernel.User));
        }

        Process process = Owner(resource);
        return ValueTask.FromResult(Of(resource) == File.Directory
            ? new ResourceStat(resource, Format(process.Pid), Directory, 0, 0, 0, process.User, process.User, process.User)
            : new ResourceStat(resource, Files[(int)Of(resource) - 1].Name, Permissions(resource), 0, 0, 0, process.User, process.User, process.User));
    }

    // procopen: args and noteid open for reading by anyone, and for writing as note does; notepg only
    // for writing, remembering the group it posts to. None may open another's files, unless they post
    // notes within its own note group; then devopen's check of the permissions.
    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        string handle = $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";
        if (resource.Identity.Path == 0 || Of(resource) == File.Directory)
        {
            if (resource.Identity.Path != 0)
            {
                Owner(resource);
            }

            return ValueTask.FromResult(new ResourceOpenHandle(resource, handle, mode, 0));
        }

        Process process = Owner(resource);
        Process? caller = kernel.Processes.GetValueOrDefault(context.ProcessId);
        File file = Of(resource);
        if (file == File.NotePg)
        {
            if ((mode & 3) != NinePConstants.OWRITE)
            {
                throw new IOException(Errors.Permission);
            }

            handle = $"{process.NoteId.ToString(CultureInfo.InvariantCulture)}:{handle}";
        }

        if (!(file is File.Note or File.NotePg && caller?.NoteId == process.NoteId))
        {
            NoNone(caller, process);
        }

        uint permissions = Permissions(resource);
        int shift = context.User == process.User ? 0 : context.User == kernel.User ? 3 : 6;
        if ((Access[mode & 3] & (permissions << shift)) != Access[mode & 3])
        {
            throw new IOException(Errors.Permission);
        }

        return ValueTask.FromResult(new ResourceOpenHandle(resource, handle, mode, 0));
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        if (openHandle.Resource.Identity.Path == 0 || Of(openHandle.Resource) == File.Directory)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        Process process = Owner(openHandle.Resource);
        switch (Of(openHandle.Resource))
        {
            case File.Args:
                return Slice(Encoding.UTF8.GetBytes(process.Args), offset, count);
            case File.Fd:
                return Line(Encoding.UTF8.GetBytes(await process.FdsAsync()), offset, count);
            case File.NoteId:
                return Slice(Encoding.ASCII.GetBytes(Number(process.NoteId)), offset, count);
            case File.Note:
                if (count < 1)
                {
                    throw new IOException(Errors.TooSmall);
                }

                if (process.TakeNote() is not { } note)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }

                byte[] text = Encoding.UTF8.GetBytes(note);
                int length = (int)Math.Min(count, (uint)text.Length + 1);
                return (byte[])[.. text[..(length - 1)], 0];
            default:
                throw new IOException(Errors.Permission);
        }
    }

    // procwrite: each write is less than ERRMAX bytes, and args and noteid are written whole.
    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (data.Length >= Process.ErrMax)
        {
            throw new IOException(Errors.TooBig);
        }

        string text = Text(data.Span);
        File file = Of(openHandle.Resource);
        if (file == File.NotePg)
        {
            kernel.PostNoteGroup(long.Parse(openHandle.HandleId[..openHandle.HandleId.IndexOf(':', StringComparison.Ordinal)], CultureInfo.InvariantCulture), text, kernel.Processes.GetValueOrDefault(context.ProcessId));
            return ValueTask.FromResult((uint)data.Length);
        }

        Process process = Owner(openHandle.Resource);
        if (file is File.Args or File.NoteId && offset != 0)
        {
            throw new IOException(Errors.TooBig);
        }

        switch (file)
        {
            case File.Args:
                process.Args = text;
                break;
            case File.Note:
                if (!process.PostNote(text))
                {
                    throw new IOException(Errors.NoteNotPosted);
                }

                break;
            case File.NoteId:
                kernel.ChangeNoteId(kernel.Processes.GetValueOrDefault(context.ProcessId), process, (uint)Libc.Strtol(text, 10, out _));
                break;
            default:
                throw new IOException(Errors.Permission);
        }

        return ValueTask.FromResult((uint)data.Length);
    }

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    // nonone: none may not touch another's process, unless it is the host owner.
    internal static void NoNone(Process? caller, Process process, string eve)
    {
        if (caller is not null && caller != process && caller.User == "none" && eve != "none")
        {
            throw new IOException(Errors.Permission);
        }
    }

    private static ResourceHandle Handle(ulong path, QidType type) => new(new ResourceIdentity(Provider, "#p", path), type);

    private static File Of(ResourceHandle resource) => (File)(resource.Identity.Path % Slots);

    private static string Format(long pid) => pid.ToString(CultureInfo.InvariantCulture);

    // readnum: right-aligned in NUMSIZE-1 places and a space.
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture).PadLeft(11) + " ";

    // procread of fd: what is left of the line the offset is in, as readfd1 gives a line a read.
    private static ReadOnlyMemory<byte> Line(byte[] data, ulong offset, uint count)
    {
        int start = (int)Math.Min(offset, (ulong)data.Length);
        int end = Array.IndexOf(data, (byte)'\n', start) + 1;
        return data.AsMemory(start, (int)Math.Min(count, (uint)((end == 0 ? data.Length : end) - start)));
    }

    private static ReadOnlyMemory<byte> Slice(byte[] bytes, ulong offset, uint count)
    {
        ReadOnlyMemory<byte> rest = bytes.AsMemory((int)Math.Min(offset, (ulong)bytes.Length));
        return rest[..(int)Math.Min(count, (uint)rest.Length)];
    }

    // What was written, as the kernel's buffer holds it: up to the first NUL.
    private static string Text(ReadOnlySpan<byte> data)
    {
        int nul = data.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? data : data[..nul]);
    }

    private void NoNone(Process? caller, Process process) => NoNone(caller, process, kernel.User);

    // procgen: a file's own permissions with the procmode's read bits, or the procmode for none.
    private uint Permissions(ResourceHandle resource)
    {
        uint permissions = Files[(int)Of(resource) - 1].Permissions;
        return permissions == 0 ? ProcMode : permissions | (ProcMode & 0b100_100_100);
    }

    private Process Owner(ResourceHandle resource)
        => kernel.Processes.GetValueOrDefault((long)(resource.Identity.Path / Slots)) is { Exited: false } process
            ? process
            : throw new IOException(Errors.ProcessExited);
}
