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
        devices = new ProcessDevices(this, kernel.Files, kernel.Pipes);
        (plane, calls) = Bind();
    }

    public long Pid => process.Id;

    public string Text { get; private set; }

    public string User { get; internal set; }

    internal RamFs Environment { get; private set; }

    internal DupDevice Dup { get; }

    internal ConsDevice Cons { get; }

    internal long ParentPid => process.ParentId ?? 0;

    public async ValueTask<int> OpenAsync(string path, int mode)
    {
        try
        {
            return await calls.OpenAsync(path, new Plan9OpenRequest((byte)mode));
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.ResourceNotFound)
        {
            throw await NotFoundAsync(path);
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
        try
        {
            return await calls.CreateAsync(path, new Plan9CreateRequest(permissions, mode));
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

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(int fd, int count, CancellationToken cancellationToken = default)
    {
        CheckMode(fd, NinePConstants.OREAD);
        try
        {
            return await calls.ReadAsync(fd, (uint)count, cancellationToken);
        }
        catch (ArgumentException)
        {
            throw new SyscallException(Errors.BadFd);
        }
        catch (IOException error)
        {
            throw new SyscallException(error.Message);
        }
    }

    public async ValueTask<int> WriteAsync(int fd, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        CheckMode(fd, NinePConstants.OWRITE);
        try
        {
            return (int)await calls.WriteAsync(fd, data, cancellationToken);
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
        catch (IOException error)
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
        ResourceStat stat = await plane.StatAsync(await ResolveAsync(path), CancellationToken.None);
        int size = 49 + new[] { stat.Name, stat.User, stat.Group, stat.LastModifier }.Sum(Encoding.UTF8.GetByteCount);
        return new Stat((ushort)size, 0, 0, stat.Resource.Qid, stat.Mode, stat.AccessTime, stat.ModificationTime, stat.Length, stat.Name, stat.User, stat.Group, stat.LastModifier);
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
    }

    public async ValueTask BindAsync(string name, string old, MountFlags flags)
    {
        try
        {
            await new NamespaceSyscalls(devices).BindAsync(process, name, old, flags);
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.ResourceNotFound)
        {
            throw await NotFoundAsync(await ResolvesAsync(name) ? old : name);
        }
        catch (NamespaceException error) when (error.Error == NamespaceError.MountTypeMismatch)
        {
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
        byte[] header = await ReadHeaderAsync(path);
        ProgramMain? program;
        int indir = 0;
        while ((program = ManagedProgram(header)) is null)
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
            header = await ReadHeaderAsync(tokens[0]);
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

    private static string[] Elements(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(name => name != ".").ToArray();

    private ProgramMain? ManagedProgram(byte[] header)
    {
        int newline = Array.IndexOf(header, (byte)'\n');
        return header.AsSpan().StartsWith(Managed) && newline != -1
            ? kernel.Program(Encoding.UTF8.GetString(header, Managed.Length, newline - Managed.Length))
            : null;
    }

    private async ValueTask<int> OpenDescriptorAsync(int fd, int mode)
    {
        CheckMode(fd, mode);
        return await DupAsync(fd, -1);
    }

    // fdtochan's check of the mode against the descriptor's: any mode for one opened for reading and
    // writing, otherwise its own, with OEXEC counting as OREAD. One not open is left to the call.
    private void CheckMode(int fd, int mode)
    {
        DescriptorSlot? slot = process.Descriptors.Snapshot().FirstOrDefault(slot => slot.Number == fd);
        int opened = slot is null ? NinePConstants.ORDWR : DupDevice.OpenMode(slot.Handle.Mode);
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
        return (data, new Plan9FileSyscalls(process, data, () => kernel.Context(Pid, User)));
    }

    private RamFs EnvironmentFor(RforkFlags flags)
        => (flags & RforkFlags.Envg) != 0 ? Environment.Copy() : (flags & RforkFlags.Cenvg) != 0 ? kernel.NewEnvironment() : Environment;

    private async ValueTask<bool> ResolvesAsync(string path)
    {
        string[] names = Elements(path);
        return (await plane.WalkAsync(Start(path), names, CancellationToken.None)).Complete(names.Length);
    }

    private async Task<byte[]> ReadHeaderAsync(string path)
    {
        NamespaceChannel file = await ResolveAsync(path);
        if (file.Current.IsDirectory)
        {
            throw new SyscallException(Errors.Name(path, Elements(path).Length, Errors.ExecDirectory));
        }

        ResourceOpenHandle opened = await plane.OpenAsync(file, NinePConstants.OEXEC, kernel.Context(Pid, User), CancellationToken.None);
        try
        {
            return (await plane.ReadAsync(opened, 0, HeaderSize, CancellationToken.None)).ToArray();
        }
        finally
        {
            await plane.ClunkAsync(opened, kernel.Context(Pid, User), CancellationToken.None);
        }
    }

    private async Task<NamespaceChannel> ResolveAsync(string path)
    {
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
