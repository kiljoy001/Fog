using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Channels;
using NinePSharp.Constants;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

public sealed class Process
{
    private const int HeaderSize = 256;
    private const int MaxInterpreterArguments = 32;
    private const int MaxIndirection = 8;
    private static readonly byte[] Managed = "\0fog "u8.ToArray();
    private readonly Channel<Waitmsg> waits = Channel.CreateUnbounded<Waitmsg>();
    private readonly FogKernel kernel;
    private readonly VProcess process;
    private readonly Process? parent;
    private readonly ProcessDevices devices;
    private LocalNamespaceDataPlane plane;
    private Plan9FileSyscalls calls;
    private int children;

    internal Process(FogKernel kernel, VProcess process, Process? parent, RamFs environment, string text, string user)
    {
        this.kernel = kernel;
        this.process = process;
        this.parent = parent;
        Environment = environment;
        Dup = new DupDevice(process, kernel.User);
        Cons = new ConsDevice(this, kernel.User);
        Text = text;
        User = user;
        devices = new ProcessDevices(this, kernel.Files, kernel.Pipes, kernel.Slash, kernel.Mounts);
        (plane, calls) = Bind();
    }

    /// <summary>nulldir: a directory entry every field of which wstat leaves unchanged.</summary>
    public static Stat NullDir => new(
        0,
        ushort.MaxValue,
        uint.MaxValue,
        new Qid((QidType)0xFF, uint.MaxValue, ulong.MaxValue),
        uint.MaxValue,
        uint.MaxValue,
        uint.MaxValue,
        ulong.MaxValue,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);

    public long Pid => process.Id;

    public string Text { get; private set; }

    public string User { get; internal set; }

    internal RamFs Environment { get; private set; }

    internal DupDevice Dup { get; }

    internal ConsDevice Cons { get; }

    internal long ParentPid => process.ParentId ?? 0;

    public async ValueTask<int> OpenAsync(string path, int mode)
    {
        Namec(path);
        try
        {
            return await NamedAsync(await calls.OpenAsync(path, new Plan9OpenRequest((byte)mode)), path);
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.ResourceNotFound)
        {
            throw await NotFoundAsync(path);
        }
        catch (NamespaceFidException)
        {
            // devopen: a directory opens only for reading.
            throw new SyscallException(Errors.Permission);
        }
        catch (DupOpenException dup)
        {
            return await OpenDescriptorAsync(dup.Descriptor, mode);
        }
        catch (IOException error)
        {
            throw new SyscallException(error.Message);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new SyscallException(Errors.NoFd);
        }
    }

    public async ValueTask<int> CreateAsync(string path, int mode, uint permissions)
    {
        Namec(path);
        try
        {
            return await NamedAsync(await calls.CreateAsync(path, new Plan9CreateRequest(permissions, mode)), path);
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.ResourceNotFound)
        {
            throw await NotFoundAsync(path);
        }
        catch (DupOpenException dup)
        {
            return await OpenDescriptorAsync(dup.Descriptor, mode);
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.CreateNotPermitted)
        {
            throw new SyscallException(Errors.Name(path, Elements(path).Length, Errors.NoCreate));
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.ResourceNotDirectory)
        {
            throw new SyscallException(Errors.Name(path, Elements(path).Length, Errors.CreateNonDirectory));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new SyscallException(Errors.NoFd);
        }
    }

    // newfd2: both ends get descriptors, or neither does.
    public async ValueTask<(int First, int Second)> PipeAsync()
    {
        var (first, second) = kernel.Pipes.Create();
        int fd = await OpenPipeAsync(first);
        try
        {
            return (fd, await OpenPipeAsync(second));
        }
        catch (SyscallException)
        {
            await CloseAsync(fd);
            throw;
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(int fd, int count, CancellationToken cancellationToken = default)
        => PReadAsync(fd, count, -1, cancellationToken);

    public ValueTask<int> WriteAsync(int fd, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => PWriteAsync(fd, data, -1, cancellationToken);

    /// <summary>pread: reads at an offset without moving the descriptor's; -1 reads at and advances it, as read does.</summary>
    public async ValueTask<ReadOnlyMemory<byte>> PReadAsync(int fd, int count, long offset, CancellationToken cancellationToken = default)
    {
        await CheckModeAsync(fd, NinePConstants.OREAD);
        try
        {
            return await calls.PReadAsync(fd, offset, (uint)count, cancellationToken);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
        catch (Exception error) when (error is IOException or NamespaceFidException)
        {
            throw new SyscallException(error.Message);
        }
    }

    /// <summary>pwrite: writes at an offset without moving the descriptor's; -1 writes at and advances it, as write does.</summary>
    public async ValueTask<int> PWriteAsync(int fd, ReadOnlyMemory<byte> data, long offset, CancellationToken cancellationToken = default)
    {
        await CheckModeAsync(fd, NinePConstants.OWRITE);
        try
        {
            return (int)await calls.PWriteAsync(fd, offset, data, cancellationToken);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
        catch (PipeClosedException)
        {
            // The note's default action, until processes can catch notes.
            throw new ExitException("sys: write on closed pipe");
        }
        catch (Exception error) when (error is IOException or NamespaceFidException)
        {
            throw new SyscallException(error.Message);
        }
    }

    /// <summary>fstat: the directory entry of an open file, under the name it was opened by.</summary>
    public async ValueTask<Stat> FStatAsync(int fd)
    {
        try
        {
            ReadOnlyMemory<byte> record = await calls.FStatAsync(fd, ushort.MaxValue);
            int offset = 0;
            return new Stat(record.Span, ref offset);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
    }

    /// <summary>wstat: changes a file's directory entry, leaving every field set as in <see cref="NullDir"/>.</summary>
    public async ValueTask WStatAsync(string path, Stat stat)
    {
        ValidStat(stat);
        await ResolveAsync(path);
        try
        {
            await calls.WStatAsync(path, KernelFileStats.Encode(stat));
        }
        catch (IOException error)
        {
            throw new SyscallException(error.Message);
        }
        catch (NamespaceFidException)
        {
            // wstat: a mount point is not renamed, and is named in full, as chanpath gives it.
            throw new SyscallException(Errors.Name(path, Math.Max(Elements(path).Length, 1), Errors.IsMountPoint));
        }
    }

    /// <summary>fwstat: as wstat, for the file a descriptor names.</summary>
    public async ValueTask FWStatAsync(int fd, Stat stat)
    {
        ValidStat(stat);
        try
        {
            await calls.FWStatAsync(fd, KernelFileStats.Encode(stat));
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
        catch (Exception error) when (error is IOException or NamespaceFidException)
        {
            throw new SyscallException(error.Message);
        }
    }

    public async ValueTask CloseAsync(int fd)
    {
        try
        {
            await process.Descriptors.CloseAsync(fd);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
    }

    public async ValueTask ChdirAsync(string path)
    {
        NamespaceChannel directory = await ResolveAsync(path);
        if (!directory.Current.IsDirectory)
        {
            throw new SyscallException(Errors.Name(path, Elements(path).Length, Errors.NotDirectory));
        }

        process.ChangeDirectory(directory);
    }

    public async ValueTask RemoveAsync(string path)
    {
        NamespaceChannel file = await ResolveAsync(path);
        try
        {
            await plane.RemoveAsync(file, null, kernel.Context(Pid, User), CancellationToken.None);
        }
        catch (IOException error)
        {
            throw new SyscallException(error.Message);
        }
    }

    public async ValueTask<Stat> StatAsync(string path)
    {
        NamespaceChannel channel = await ResolveAsync(path);
        try
        {
            return KernelFileStats.ToStat(await plane.StatAsync(channel, CancellationToken.None));
        }
        catch (IOException error)
        {
            throw new SyscallException(error.Message);
        }
    }

    public async ValueTask<long> SeekAsync(int fd, long offset, int whence)
    {
        try
        {
            return await calls.SeekAsync(fd, offset, (Plan9SeekWhence)whence);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
        catch (NamespaceFidException error)
        {
            // sysseek: a directory only back to its start, and nothing before a file's.
            throw new SyscallException(error.Message == "negative offset" ? Errors.NegativeOffset : Errors.IsDirectory);
        }
    }

    // bindmount for mount: the descriptor's connection is versioned the first time, attached as the
    // caller, and what it attached bound on old; then the descriptor is closed. Mounting with an
    // authentication descriptor is not implemented yet.
    public async ValueTask MountAsync(int fd, int afd, string old, MountFlags flags, string aname)
    {
        if (afd >= 0)
        {
            throw new NotSupportedException("mount with an authentication descriptor is not implemented");
        }

        if (process.ProcessGroup.MountTable.MountsDisabled)
        {
            throw new SyscallException(Errors.NoAttach);
        }

        DescriptorLease transport;
        try
        {
            transport = process.Descriptors.Acquire(fd);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }

        if ((transport.Handle.Mode & 3) != NinePConstants.ORDWR)
        {
            await transport.DisposeAsync();
            throw new SyscallException(Errors.BadUseFd);
        }

        MountTree tree;
        try
        {
            MountConnection connection = await kernel.Mounts.ConnectAsync(transport, devices.Device(transport.Handle.Resource), () => kernel.Context(Pid, User));
            tree = await kernel.Mounts.AttachAsync(connection, User, aname, kernel.MountFids);
        }
        catch (IOException error)
        {
            throw new SyscallException(error.Message);
        }

        var source = new NamespaceMountSource(NamespaceChannel.Restore([new ChannelFrame("/", tree.Root)]), NinePConstants.ORDWR, aname, closeAsync: () => CloseAsync(fd));
        try
        {
            await new NamespaceSyscalls(devices).MountAsync(process, source, old, flags);
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.ResourceNotFound)
        {
            throw await NotFoundAsync(old);
        }
        catch (NamespaceException error) when (error.Error is NamespaceError.MountTypeMismatch or NamespaceError.MountTargetMustBeDirectory)
        {
            throw new SyscallException(Errors.Mount);
        }
    }

    // bindmount: the first name is resolved before the second is looked at.
    public async ValueTask BindAsync(string name, string old, MountFlags flags)
    {
        Namec(name);
        if (NamecProblem(old) is { } problem)
        {
            throw await ResolvesAsync(name) ? new SyscallException(problem) : await NotFoundAsync(name);
        }

        try
        {
            await new NamespaceSyscalls(devices).BindAsync(process, name, old, flags);
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.ResourceNotFound)
        {
            throw await NotFoundAsync(await ResolvesAsync(name) ? old : name);
        }
        catch (NamespaceException error) when (error.Error is NamespaceError.MountTypeMismatch or NamespaceError.UnionRequiresDirectory or NamespaceError.CreateBindNotPermitted)
        {
            // cmount: Emount, also for a union of files, and for a bind with creation of a union or of a
            // mount that takes none.
            throw new SyscallException(Errors.Mount);
        }
    }

    public async ValueTask RforkAsync(RforkFlags flags)
    {
        if ((flags & (RforkFlags.Mem | RforkFlags.Nowait)) != 0)
        {
            throw new SyscallException(Errors.BadArg);
        }

        Validate(flags);

        // Without its flags, each group is shared, which leaves it as it is.
        await kernel.Table.RforkDescriptorsAsync(Pid, DescriptorMode(flags));
        kernel.Table.RforkNamespace(Pid, NamespaceMode(flags));
        (plane, calls) = Bind();
        Environment = EnvironmentFor(flags);
    }

    public async ValueTask<int> DupAsync(int fd, int target)
    {
        try
        {
            return await process.Descriptors.DuplicateAsync(fd, target);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
    }

    /// <summary>WASI's fd_renumber: moves a descriptor to another number, closing what that number named.</summary>
    public async ValueTask RenumberAsync(int fd, int target)
    {
        try
        {
            await process.Descriptors.RenumberAsync(fd, target);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
    }

    public async ValueTask<IReadOnlyList<Stat>> DirReadAsync(int fd)
    {
        var entries = new List<Stat>();
        for (ReadOnlyMemory<byte> block; !(block = await ReadAsync(fd, 8192)).IsEmpty;)
        {
            for (int offset = 0; offset < block.Length;)
            {
                entries.Add(new Stat(block.Span, ref offset));
            }
        }

        return entries;
    }

    public long Fork(RforkFlags flags, Func<Process, Task> body)
    {
        Validate(flags);
        VProcess forked = kernel.Table.Fork(Pid, NamespaceMode(flags), descriptorMode: DescriptorMode(flags));
        var child = new Process(kernel, forked, this, EnvironmentFor(flags), Text, User);
        Interlocked.Increment(ref children);

        _ = Task.Run(() => child.RunAsync(body));
        return child.Pid;
    }

    public async Task ExecAsync(string path, IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
        {
            throw new SyscallException(Errors.BadArg);
        }

        string elem = path[(path.LastIndexOf('/') + 1)..];
        var interpreter = new List<string>();
        string image = path;
        byte[] header = await ReadHeaderAsync(image);
        ProgramMain? program;
        int indir = 0;
        while ((program = await LoadAsync(image, header)) is null)
        {
            int newline = header.AsSpan().StartsWith("#!"u8) ? Array.IndexOf(header, (byte)'\n') : -1;
            if (newline == -1)
            {
                throw new SyscallException(Errors.BadExec);
            }

            if (indir == 0)
            {
                interpreter.Add(path);
            }
            else if (indir >= MaxIndirection)
            {
                throw new SyscallException(Errors.BadExec);
            }

            List<string> tokens = Tokenize(Line(header.AsSpan(2, newline - 2)), MaxInterpreterArguments + 1);
            if (tokens.Count < 1 || tokens.Count > MaxInterpreterArguments)
            {
                throw new SyscallException(Errors.BadExec);
            }

            interpreter.InsertRange(0, tokens);
            image = tokens[0];
            header = await ReadHeaderAsync(image);
            indir++;
        }

        List<string> arguments = argv.ToList();
        if (indir > 0)
        {
            interpreter[0] = elem;
            arguments = [.. interpreter, .. argv.Skip(1)];
        }

        await process.Descriptors.CloseOnExecAsync();
        Text = elem;
        await program(this, arguments);
        Exits("main");
    }

    // A loader's read of the image it claimed, as exec opens it; one longer than limit bytes is an
    // invalid exec header.
    public async Task<byte[]> ReadImageAsync(string path, int limit)
    {
        ResourceOpenHandle opened = await OpenExecutableAsync(path);
        try
        {
            var image = new MemoryStream();
            for (ReadOnlyMemory<byte> block; !(block = await plane.ReadAsync(opened, (ulong)image.Length, 8192, CancellationToken.None)).IsEmpty;)
            {
                if (image.Length + block.Length > limit)
                {
                    throw new SyscallException(Errors.BadExec);
                }

                image.Write(block.Span);
            }

            return image.ToArray();
        }
        finally
        {
            await plane.ClunkAsync(opened, kernel.Context(Pid, User), CancellationToken.None);
        }
    }

    /// <summary>fd2path: the rooted name the descriptor's file was opened or created by.</summary>
    public async ValueTask<string> Fd2PathAsync(int fd)
    {
        ResourceOpenHandle handle = await HandleAsync(fd) ?? throw new SyscallException(Errors.BadFd);

        // Every descriptor not opened or created by name is a pipe's end, which devpipe names.
        return kernel.Names.TryGetValue(handle, out string? name) ? name : "#|/" + (await FStatAsync(fd)).Name;
    }

    [DoesNotReturn]
    public void Exits(string? status) => throw new ExitException(status ?? string.Empty);

    public async Task<Waitmsg> WaitAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref children) == 0)
        {
            throw new SyscallException(Errors.NoChild);
        }

        Waitmsg message = await waits.Reader.ReadAsync(cancellationToken);
        Interlocked.Decrement(ref children);
        return message;
    }

    internal static List<string> Tokenize(string line, int max)
    {
        var tokens = new List<string>();
        int t = 0;
        while (tokens.Count < max)
        {
            while (t < line.Length && Separator(line[t]))
            {
                t++;
            }

            if (t == line.Length)
            {
                break;
            }

            var token = new StringBuilder();
            bool quoting = false;
            while (t < line.Length && (quoting || !Separator(line[t])))
            {
                if (line[t] != '\'')
                {
                    token.Append(line[t++]);
                }
                else if (!quoting)
                {
                    quoting = true;
                    t++;
                }
                else if (t + 1 == line.Length || line[t + 1] != '\'')
                {
                    quoting = false;
                    t++;
                }
                else
                {
                    token.Append('\'');
                    t += 2;
                }
            }

            tokens.Add(token.ToString());
        }

        return tokens;
    }

    // Notes and rendezvous do not exist yet, so their groups need nothing.
    // What a pipe end has queued and whether it has hung up, for a program that waits on readiness;
    // null for a descriptor that is not a pipe's end.
    internal async ValueTask<(long Queued, bool HungUp)?> PipeReadinessAsync(int fd)
    {
        ResourceHandle end = (await HandleAsync(fd) ?? throw new SyscallException(Errors.BadFd)).Resource;
        return end.Identity.Provider == PipeDevice.Provider ? kernel.Pipes.Readiness(end) : null;
    }

    private static void Validate(RforkFlags flags)
    {
        static bool Both(RforkFlags flags, RforkFlags a, RforkFlags b) => (flags & (a | b)) == (a | b);
        if (Both(flags, RforkFlags.Fdg, RforkFlags.Cfdg) || Both(flags, RforkFlags.Nameg, RforkFlags.Cnameg) || Both(flags, RforkFlags.Envg, RforkFlags.Cenvg))
        {
            throw new SyscallException(Errors.BadArg);
        }

        const RforkFlags implemented = RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Cfdg | RforkFlags.Nameg | RforkFlags.Cnameg
            | RforkFlags.Envg | RforkFlags.Cenvg | RforkFlags.Noteg | RforkFlags.Rend;
        if ((flags & ~implemented) != 0)
        {
            throw new NotSupportedException($"rfork flags {flags} are not implemented");
        }
    }

    private static NamespaceForkMode NamespaceMode(RforkFlags flags)
        => (flags & RforkFlags.Nameg) != 0 ? NamespaceForkMode.Copy : (flags & RforkFlags.Cnameg) != 0 ? NamespaceForkMode.Empty : NamespaceForkMode.Share;

    private static DescriptorForkMode DescriptorMode(RforkFlags flags)
        => (flags & RforkFlags.Fdg) != 0 ? DescriptorForkMode.Copy : (flags & RforkFlags.Cfdg) != 0 ? DescriptorForkMode.Empty : DescriptorForkMode.Share;

    private static bool Separator(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static string Line(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? bytes : bytes[..nul]);
    }

    // namec's checks of a name before it is evaluated.
    private static string? NamecProblem(string path)
        => path.Length == 0 ? Errors.EmptyName : Errors.NameProblem(path, slashOk: true) ?? (Errors.Blank(path) ? Errors.BlankName : null);

    private static void Namec(string path)
    {
        if (NamecProblem(path) is { } problem)
        {
            throw new SyscallException(problem);
        }
    }

    // validstat: a new name, unless it is "/", may not hold a slash or a bad character; one too long
    // for its 64-byte buffer is left to the file server. Fog refuses one of only white space, whatever its length.
    private static void ValidStat(Stat stat)
    {
        if (Encoding.UTF8.GetByteCount(stat.Name) + 1 <= 64 && stat.Name != "/" && Errors.NameProblem(stat.Name, slashOk: false) is { } problem)
        {
            throw new SyscallException(problem);
        }

        if (stat.Name.Length > 0 && string.IsNullOrWhiteSpace(stat.Name))
        {
            throw new SyscallException(Errors.BlankName);
        }
    }

    private static string[] Elements(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(name => name != ".").ToArray();

    // cleanname: elements of . and empty ones dropped, and .. taking away the one before, if any.
    private static string Clean(string name)
    {
        var kept = new List<string>();
        foreach (string element in name.Split('/'))
        {
            if (element == "..")
            {
                if (kept.Count > 0)
                {
                    kept.RemoveAt(kept.Count - 1);
                }
            }
            else if (element is not ("" or "."))
            {
                kept.Add(element);
            }
        }

        return "/" + string.Join('/', kept);
    }

    private ProgramMain? ManagedProgram(byte[] header)
    {
        int newline = Array.IndexOf(header, (byte)'\n');
        return header.AsSpan().StartsWith(Managed) && newline != -1
            ? kernel.Program(Encoding.UTF8.GetString(header, Managed.Length, newline - Managed.Length))
            : null;
    }

    private async ValueTask<int> OpenDescriptorAsync(int fd, int mode)
    {
        await CheckModeAsync(fd, mode);
        return await DupAsync(fd, -1);
    }

    // fdtochan's check of the mode against the descriptor's: any mode for one opened for reading and
    // writing, otherwise its own, with OEXEC counting as OREAD. One not open is left to the call.
    private async ValueTask CheckModeAsync(int fd, int mode)
    {
        ResourceOpenHandle? handle = await HandleAsync(fd);
        int opened = handle is null ? NinePConstants.ORDWR : DupDevice.OpenMode(handle.Mode);
        if (opened != NinePConstants.ORDWR && DupDevice.OpenMode(mode) != opened)
        {
            throw new SyscallException(Errors.BadUseFd);
        }
    }

    private async ValueTask<int> OpenPipeAsync(ResourceHandle end)
    {
        ResourceOpenHandle opened = await kernel.Pipes.OpenAsync(end, NinePConstants.ORDWR, kernel.Context(Pid, User), CancellationToken.None);
        ResourceOperationContext close = kernel.Context(Pid, User);
        try
        {
            return process.Descriptors.Install(opened, () => kernel.Pipes.ClunkAsync(opened, close, CancellationToken.None));
        }
        catch (ArgumentOutOfRangeException)
        {
            await kernel.Pipes.ClunkAsync(opened, close, CancellationToken.None);
            throw new SyscallException(Errors.NoFd);
        }
    }

    private (LocalNamespaceDataPlane Plane, Plan9FileSyscalls Calls) Bind()
    {
        var data = new LocalNamespaceDataPlane(process.ProcessGroup.MountTable, devices);
        return (data, new Plan9FileSyscalls(process, data, () => kernel.Context(Pid, User), fileStats: new KernelFileStats(devices)));
    }

    private RamFs EnvironmentFor(RforkFlags flags)
        => (flags & RforkFlags.Envg) != 0 ? Environment.Copy() : (flags & RforkFlags.Cenvg) != 0 ? kernel.NewEnvironment() : Environment;

    private async ValueTask<bool> ResolvesAsync(string path)
    {
        string[] names = Elements(path);
        return (await plane.WalkAsync(Start(path), names, CancellationToken.None)).Complete(names.Length);
    }

    // A managed program first, then whichever loader claims the header.
    private async ValueTask<int> NamedAsync(int fd, string path)
    {
        string rooted = path.StartsWith('/') ? path : $"/{string.Join('/', process.CurrentDirectory.VisiblePath)}/{path}";
        if (await HandleAsync(fd) is { } handle)
        {
            kernel.Names.AddOrUpdate(handle, Clean(rooted));
        }

        return fd;
    }

    // The handle open as fd, looked up without copying the table; null when fd is not open.
    private async ValueTask<ResourceOpenHandle?> HandleAsync(int fd)
    {
        DescriptorLease lease;
        try
        {
            lease = process.Descriptors.Acquire(fd);
        }
        catch (ArgumentException)
        {
            return null;
        }

        await using (lease)
        {
            return lease.Handle;
        }
    }

    private async ValueTask<ProgramMain?> LoadAsync(string path, byte[] header)
    {
        if (ManagedProgram(header) is { } managed)
        {
            return managed;
        }

        foreach (ExecLoader loader in kernel.Loaders)
        {
            if (await loader(this, path, header) is { } program)
            {
                return program;
            }
        }

        return null;
    }

    private async Task<byte[]> ReadHeaderAsync(string path)
    {
        ResourceOpenHandle opened = await OpenExecutableAsync(path);
        try
        {
            return (await plane.ReadAsync(opened, 0, HeaderSize, CancellationToken.None)).ToArray();
        }
        finally
        {
            await plane.ClunkAsync(opened, kernel.Context(Pid, User), CancellationToken.None);
        }
    }

    private async Task<ResourceOpenHandle> OpenExecutableAsync(string path)
    {
        NamespaceChannel file = await ResolveAsync(path);
        if (file.Current.IsDirectory)
        {
            throw new SyscallException(Errors.Name(path, Elements(path).Length, Errors.ExecDirectory));
        }

        return await plane.OpenAsync(file, NinePConstants.OEXEC, kernel.Context(Pid, User), CancellationToken.None);
    }

    private async Task<NamespaceChannel> ResolveAsync(string path)
    {
        Namec(path);
        string[] names = Elements(path);
        NamespaceWalkResult walked = await plane.WalkAsync(Start(path), names, CancellationToken.None);
        return walked.Complete(names.Length) ? walked.Channel : throw NameError(path, walked);
    }

    private async Task<SyscallException> NotFoundAsync(string path)
        => NameError(path, await plane.WalkAsync(Start(path), Elements(path), CancellationToken.None));

    private SyscallException NameError(string path, NamespaceWalkResult walked)
        => walked.Channel.Current.IsDirectory
            ? new(Errors.Name(path, walked.Qids.Count + 1, Errors.NotExist))
            : new(Errors.Name(path, walked.Qids.Count, Errors.NotDirectory));

    private NamespaceChannel Start(string path) => path.StartsWith('/') ? process.Root.Clone() : process.CurrentDirectory.Clone();

    private async Task RunAsync(Func<Process, Task> body)
    {
        string status;
        try
        {
            await body(this);
            status = "main";
        }
        catch (ExitException exit)
        {
            status = exit.Message;
        }
        catch (Exception error)
        {
            status = "sys: " + error.Message;
        }

        await kernel.Table.TerminateAsync(Pid);
        parent!.Deliver(new Waitmsg(Pid, status.Length == 0 ? string.Empty : $"{Text} {Pid}: {status}"));
    }

    private void Deliver(Waitmsg message) => waits.Writer.TryWrite(message);
}
