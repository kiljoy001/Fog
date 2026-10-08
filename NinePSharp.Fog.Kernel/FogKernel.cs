using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

public sealed class FogKernel
{
    private readonly IReadOnlyDictionary<string, ProgramMain> programs;
    private readonly ResourceHandle root;
    private readonly string session = Guid.NewGuid().ToString();
    private long sequence;

    public FogKernel(IReadOnlyDictionary<string, ProgramMain> programs, IResourceDataOperations files, ResourceHandle root, string user = "none")
    {
        this.programs = programs;
        this.root = root;
        Files = files;
        User = user;
        Pipes = new PipeDevice(user);
    }

    internal VProcessTable Table { get; } = new();

    internal IResourceDataOperations Files { get; }

    internal PipeDevice Pipes { get; }

    internal string User { get; }

    public static FogKernel InMemory(IReadOnlyDictionary<string, ProgramMain> programs, string user = "none", TimeProvider? clock = null)
    {
        var files = new RamFs("ram", "#R", user, clock: clock);
        return new FogKernel(programs, files, files.Root, user);
    }

    // The root may hold what an earlier boot left: its directories are kept, and each program's file
    // is written again.
    public async Task<Process> BootAsync()
    {
        ResourceHandle bin = await DirectoryAsync("bin");
        await DirectoryAsync("tmp");
        ResourceHandle env = await DirectoryAsync("env");
        ResourceHandle fd = await DirectoryAsync("fd");
        ResourceHandle dev = await DirectoryAsync("dev");
        foreach (string name in programs.Keys)
        {
            ResourceOpenHandle program = await Files.WalkAsync(bin, name, CancellationToken.None) is { } installed
                ? await Files.OpenAsync(installed, NinePConstants.OWRITE | NinePConstants.OTRUNC, Context(0, User), CancellationToken.None)
                : await Files.CreateAndOpenAsync(bin, name, 0b111_111_101, NinePConstants.OWRITE, Context(0, User), CancellationToken.None);
            await Files.WriteAsync(program, 0, Encoding.UTF8.GetBytes($"\0fog {name}\n"), Context(0, User), CancellationToken.None);
            await Files.ClunkAsync(program, Context(0, User), CancellationToken.None);
        }

        VProcess first = Table.CreateInitial(new NamespaceNavigator(new MountTable(), Files).Attach(root));
        RamFs environment = NewEnvironment();
        first.ProcessGroup.MountTable.Mount(environment.Root, env, MountFlags.Replace | MountFlags.Create);
        first.ProcessGroup.MountTable.Mount(DupDevice.Root, fd);
        first.ProcessGroup.MountTable.Mount(ConsDevice.Root, dev, MountFlags.After);
        return new Process(this, first, null, environment, "*init*", User);
    }

    internal RamFs NewEnvironment() => new("env", "#e", User, changeable: false);

    internal ProgramMain? Program(string name) => programs.GetValueOrDefault(name);

    internal ResourceOperationContext Context(long pid, string user)
        => new(new ResourceOperationId(session, (ulong)Interlocked.Increment(ref sequence)), pid, user);

    private async Task<ResourceHandle> DirectoryAsync(string name)
        => await Files.WalkAsync(root, name, CancellationToken.None) ?? await Files.CreateAsync(root, name, true, CancellationToken.None);
}
