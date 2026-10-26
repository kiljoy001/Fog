using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

public sealed class FogKernel
{
    // The namespace newns builds, as 9front's /lib/namespace: what BootAsync composes, with the
    // store reached by its name #R where 9front mounts /srv/boot.
    public const string NamespaceFile = """
        # kernel devices
        bind #c /dev
        bind #d /fd
        bind -c #e /env
        bind #p /proc
        bind -c #s /srv
        bind -a #¤ /dev

        # root
        bind #R /root
        bind -a /root /

        # mount points
        mount -a /srv/slashn /n
        mount -a /srv/slashmnt /mnt
        mount -a /srv/mntexport /mnt/exportfs

        # standard bin
        bind -c /root/bin /bin
        bind /root/rc /rc

        # internal networks
        bind -a #I /net

        cd /usr/$user

        . /lib/namespace.local

        """;

    private static readonly string[] StoreDirectories = ["adm", "bin", "lib", "rc", "tmp", "usr"];
    private readonly IReadOnlyDictionary<string, ProgramMain> programs;
    private readonly ResourceHandle store;
    private readonly string session = Guid.NewGuid().ToString();
    private readonly object noting = new();
    private readonly TimeProvider clock = TimeProvider.System;
    private readonly SemaphoreSlim collecting = new(1, 1);
    private Keyboard? keyboard;
    private volatile string user;
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
        this.user = user;
        Loaders = loaders;
        Pipes = new PipeDevice(() => User);
        Slash = new RootDevice(() => User);
        Procs = new ProcDevice(this);
        Ip = new IpDevice(this);
        Srv = new SrvDevice(this);
        Cap = new CapDevice(this);
        Mounts.Collect = CollectMountsAsync;
    }

    // The host's clock, which #c's bintime reads, and when the kernel was made by it.
    public TimeProvider Clock
    {
        get => clock;
        init
        {
            clock = value;
            Booted = value.GetTimestamp();
        }
    }

    // The host's interfaces and resolver, which the programs that need them ask, as Fog keeps no network of its own.
    public HostNetwork Host { get; init; } = HostNetwork.System;

    // The host's console, which #c/cons writes to and reads from.
    public HostConsole Console { get; init; } = HostConsole.System;

    // The host's nvram, which #c/nvram reads and writes.
    public HostNvram Nvram { get; init; } = HostNvram.None;

    internal long Booted { get; private set; } = TimeProvider.System.GetTimestamp();

    // devcons's sysname, empty until it is written.
    internal string Sysname { get; set; } = string.Empty;

    internal VProcessTable Table { get; } = new();

    internal IResourceDataOperations Files { get; }

    internal PipeDevice Pipes { get; }

    internal RootDevice Slash { get; }

    internal MountDevice Mounts { get; } = new();

    // The store the kernel boots from, which #R names.
    internal ResourceHandle Store => store;

    internal ProcDevice Procs { get; }

    internal IpDevice Ip { get; }

    internal SrvDevice Srv { get; }

    internal CapDevice Cap { get; }

    // proctab: each process from its creation until it exits.
    internal ConcurrentDictionary<long, Process> Processes { get; } = new();

    // How many fids a mount holds for files it has only walked to.
    internal int MountFids { get; set; } = 256;

    // eve: the host owner, who may name another through #c/hostowner.
    internal string User
    {
        get => user;
        set => user = value;
    }

    // The console's input, as drawterm's kbd keeps it.
    internal Keyboard Keyboard => LazyInitializer.EnsureInitialized(ref keyboard, () => new Keyboard(Console));

    internal IReadOnlyList<ExecLoader> Loaders { get; }

    // fd2path: the rooted name each open file was opened or created by, kept with its handle, which
    // dup, renumber and fork share.
    internal ConditionalWeakTable<ResourceOpenHandle, string> Names { get; } = new();

    public static FogKernel InMemory(
        IReadOnlyDictionary<string, ProgramMain> programs,
        string user = "none",
        TimeProvider? clock = null,
        HostNetwork? host = null,
        HostConsole? console = null,
        HostNvram? nvram = null,
        params IReadOnlyList<ExecLoader> loaders)
    {
        var files = new RamFs("ram", "#R", user, clock: clock);
        return new FogKernel(programs, files, files.Root, user, loaders) { Clock = clock ?? TimeProvider.System, Host = host ?? HostNetwork.System, Console = console ?? HostConsole.System, Nvram = nvram ?? HostNvram.None };
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
            // A name such as aux/listen1 is written in the directories it names, made where missing.
            ResourceHandle directory = bin;
            string[] elements = name.Split('/');
            foreach (string element in elements[..^1])
            {
                directory = await Files.WalkAsync(directory, element, CancellationToken.None) ?? await Files.CreateAsync(directory, element, true, CancellationToken.None);
            }

            ResourceOpenHandle program = await Files.WalkAsync(directory, elements[^1], CancellationToken.None) is { } installed
                ? await Files.OpenAsync(installed, NinePConstants.OWRITE | NinePConstants.OTRUNC, Context(0, User), CancellationToken.None)
                : await Files.CreateAndOpenAsync(directory, elements[^1], 0b111_111_101, NinePConstants.OWRITE, Context(0, User), CancellationToken.None);
            await Files.WriteAsync(program, 0, Encoding.UTF8.GetBytes($"\0fog {name}\n"), Context(0, User), CancellationToken.None);
            await Files.ClunkAsync(program, Context(0, User), CancellationToken.None);
        }

        ResourceHandle lib = await DirectoryAsync("lib");
        ResourceOpenHandle namespaceFile = await Files.WalkAsync(lib, "namespace", CancellationToken.None) is { } written
            ? await Files.OpenAsync(written, NinePConstants.OWRITE | NinePConstants.OTRUNC, Context(0, User), CancellationToken.None)
            : await Files.CreateAndOpenAsync(lib, "namespace", 0b110_100_100, NinePConstants.OWRITE, Context(0, User), CancellationToken.None);
        await Files.WriteAsync(namespaceFile, 0, Encoding.UTF8.GetBytes(NamespaceFile), Context(0, User), CancellationToken.None);
        await Files.ClunkAsync(namespaceFile, Context(0, User), CancellationToken.None);

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
        mounts.Mount(ProcDevice.Root, RootDevice.MountPoint("proc"));
        mounts.Mount(IpDevice.Root, RootDevice.MountPoint("net"), MountFlags.After);
        mounts.Mount(SrvDevice.Root, RootDevice.MountPoint("srv"), MountFlags.Replace | MountFlags.Create);
        mounts.Mount(CapDevice.Root, RootDevice.MountPoint("dev"), MountFlags.After);
        var init = new Process(this, first, null, environment, "*init*", User, 0);
        await BootrcAsync(init);
        return init;
    }

    internal void Enter(Process process) => Processes[process.Pid] = process;

    // renameuser: each process of the old user becomes the new.
    internal void Renameuser(string old, string @new)
    {
        foreach (Process p in Processes.Values.Where(p => p.User == old))
        {
            p.User = @new;
        }
    }

    // A process gone, and with it what it held of any mount.
    internal void Leave(Process process)
    {
        Processes.TryRemove(process.Pid, out _);
        _ = CollectMountsAsync();
    }

    // The mounts nothing holds any more let go, as the close of the last chan that refers to a
    // mount frees its Mnt: none in a live namespace's mount table, at a process's root or dot, or
    // open; nor one between its attach and its mount.
    internal async Task CollectMountsAsync()
    {
        await collecting.WaitAsync();
        try
        {
            foreach (MountTree tree in Mounts.Trees)
            {
                var seen = new HashSet<MountTable>();
                if (!tree.Pending && !tree.HasOpenFiles && !Processes.Values.Any(p => p.Holds(tree, seen)))
                {
                    await Mounts.ReleaseAsync(tree);
                }
            }
        }
        finally
        {
            collecting.Release();
        }
    }

    // postnotepg: the note to every process of the group but the one posting it.
    internal void PostNoteGroup(long noteId, string note, Process? poster)
    {
        foreach (Process process in Processes.Values)
        {
            if (process != poster && process.NoteId == noteId)
            {
                process.PostNote(note);
            }
        }
    }

    // setnoteid with no number: the process keeps its group if nothing else holds the group's
    // number, as a pid, a parent's pid or another process's group; otherwise it takes a new number
    // from the pids'.
    internal void NewNoteGroup(Process process)
    {
        lock (noting)
        {
            long id = process.NoteId;
            int references = Processes.Values.Sum(other => (other.Pid == id ? 1 : 0) + (other.NoteId == id ? 1 : 0) + (other.ParentPid == id ? 1 : 0));
            if (references != 1)
            {
                process.NoteId = Table.ReserveId();
            }
        }
    }

    // changenoteid: a process's own pid, or the group of another process of the same user's.
    internal void ChangeNoteId(Process? caller, Process process, long noteId)
    {
        lock (noting)
        {
            if (noteId <= 0)
            {
                throw new IOException(Errors.BadArg);
            }

            if (noteId == process.NoteId || noteId == process.Pid)
            {
                process.NoteId = noteId;
                return;
            }

            if (Processes.Values.FirstOrDefault(other => other.NoteId == noteId && other.User == process.User) is not { } member)
            {
                throw new IOException(Errors.Permission);
            }

            ProcDevice.NoNone(caller, member, User);
            process.NoteId = noteId;
        }
    }

    internal RamFs NewEnvironment() => new("env", "#e", User, changeable: false);

    internal ProgramMain? Program(string name) => programs.GetValueOrDefault(name);

    internal ResourceOperationContext Context(long pid, string user)
        => new(new ResourceOperationId(session, (ulong)Interlocked.Increment(ref sequence)), pid, user);

    private async Task<ResourceHandle> DirectoryAsync(string name)
        => await Files.WalkAsync(store, name, CancellationToken.None) ?? await Files.CreateAsync(store, name, true, CancellationToken.None);

    // bootrc's mount points: mntgen on /n, /mnt and /mnt/exportfs, each posted for others to mount,
    // when the kernel has mntgen to run.
    private async Task BootrcAsync(Process init)
    {
        if (!programs.ContainsKey("mntgen"))
        {
            return;
        }

        foreach ((string srv, string mtpt) in new[] { ("slashn", "/n"), ("slashmnt", "/mnt"), ("mntexport", "/mnt/exportfs") })
        {
            init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync("/bin/mntgen", ["mntgen", "-s", srv, mtpt]));
            if ((await init.WaitAsync()).Message.Length == 0)
            {
                NinePSharp.Messages.Stat d = Process.NullDir;
                await init.WStatAsync($"/srv/{srv}", new NinePSharp.Messages.Stat(0, d.Type, d.Dev, d.Qid, 0b110_110_110, d.Atime, d.Mtime, d.Length, d.Name, d.Uid, d.Gid, d.Muid));
            }
        }
    }
}
