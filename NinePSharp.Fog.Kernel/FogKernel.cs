using System.Runtime.CompilerServices;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

public sealed class FogKernel
{
    private static readonly string[] StoreDirectories = ["adm", "bin", "lib", "rc", "tmp", "usr"];
    private readonly IReadOnlyDictionary<string, ProgramMain> programs;
    private readonly ResourceHandle store;
    private readonly string session = Guid.NewGuid().ToString();
    private long sequence;

    public FogKernel(
        IReadOnlyDictionary<string, ProgramMain> programs,
        IResourceDataOperations files,
        ResourceHandle store,
        string user = "none",
        params IReadOnlyList<ExecLoader> loaders)
    {
        this.programs = programs;
        this.store = store;
        Files = files;
        User = user;
        Loaders = loaders;
        Pipes = new PipeDevice(user);
        Slash = new RootDevice(user);
    }

    internal VProcessTable Table { get; } = new();

    internal IResourceDataOperations Files { get; }

    internal PipeDevice Pipes { get; }

    internal RootDevice Slash { get; }

    internal MountDevice Mounts { get; } = new();

    // How many fids a mount holds for files it has only walked to.
    internal int MountFids { get; set; } = 256;

    internal string User { get; }

    internal IReadOnlyList<ExecLoader> Loaders { get; }

    // fd2path: the rooted name each open file was opened or created by, kept with its handle, which
    // dup, renumber and fork share.
    internal ConditionalWeakTable<ResourceOpenHandle, string> Names { get; } = new();

    public static FogKernel InMemory(
        IReadOnlyDictionary<string, ProgramMain> programs,
        string user = "none",
        TimeProvider? clock = null,
        params IReadOnlyList<ExecLoader> loaders)
    {
        var files = new RamFs("ram", "#R", user, clock: clock);
        return new FogKernel(programs, files, files.Root, user, loaders);
    }

    // The store may hold what an earlier boot left: its directories are kept, and each program's file
    // is written again. The namespace is then built as bootrc and /lib/namespace build it, with the
    // store's /bin bound with creation in place of /$cputype/bin.
    public async Task<Process> BootAsync()
    {
        foreach (string name in StoreDirectories)
        {
            await DirectoryAsync(name);
        }

        ResourceHandle bin = await DirectoryAsync("bin");
        foreach (string name in programs.Keys)
        {
            ResourceOpenHandle program = await Files.WalkAsync(bin, name, CancellationToken.None) is { } installed
                ? await Files.OpenAsync(installed, NinePConstants.OWRITE | NinePConstants.OTRUNC, Context(0, User), CancellationToken.None)
                : await Files.CreateAndOpenAsync(bin, name, 0b111_111_101, NinePConstants.OWRITE, Context(0, User), CancellationToken.None);
            await Files.WriteAsync(program, 0, Encoding.UTF8.GetBytes($"\0fog {name}\n"), Context(0, User), CancellationToken.None);
            await Files.ClunkAsync(program, Context(0, User), CancellationToken.None);
        }

        VProcess first = Table.CreateInitial(new NamespaceNavigator(new MountTable(), Files).Attach(RootDevice.Root));
        MountTable mounts = first.ProcessGroup.MountTable;
        mounts.Mount(store, RootDevice.MountPoint("root"));
        mounts.Mount(store, RootDevice.Root, MountFlags.After);
        RamFs environment = NewEnvironment();
        mounts.Mount(ConsDevice.Root, RootDevice.MountPoint("dev"));
        mounts.Mount(DupDevice.Root, RootDevice.MountPoint("fd"));
        mounts.Mount(environment.Root, RootDevice.MountPoint("env"), MountFlags.Replace | MountFlags.Create);
        mounts.Mount(await DirectoryAsync("rc"), RootDevice.MountPoint("rc"));
        mounts.Mount(bin, RootDevice.MountPoint("bin"), MountFlags.Replace | MountFlags.Create);
        return new Process(this, first, null, environment, "*init*", User);
    }

    internal RamFs NewEnvironment() => new("env", "#e", User, changeable: false);

    internal ProgramMain? Program(string name) => programs.GetValueOrDefault(name);

    internal ResourceOperationContext Context(long pid, string user)
        => new(new ResourceOperationId(session, (ulong)Interlocked.Increment(ref sequence)), pid, user);

    private async Task<ResourceHandle> DirectoryAsync(string name)
        => await Files.WalkAsync(store, name, CancellationToken.None) ?? await Files.CreateAsync(store, name, true, CancellationToken.None);
}
