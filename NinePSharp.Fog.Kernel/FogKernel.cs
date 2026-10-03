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
    }

    internal VProcessTable Table { get; } = new();

    internal IResourceDataOperations Files { get; }

    internal string User { get; }

    public static FogKernel InMemory(IReadOnlyDictionary<string, ProgramMain> programs, string user = "none")
    {
        var files = new RamFs("ram", "#R", user);
        return new FogKernel(programs, files, files.Root, user);
    }

    public async Task<Process> BootAsync()
    {
        ResourceHandle bin = await Files.CreateAsync(root, "bin", true, CancellationToken.None);
        await Files.CreateAsync(root, "tmp", true, CancellationToken.None);
        ResourceHandle env = await Files.CreateAsync(root, "env", true, CancellationToken.None);
        foreach (string name in programs.Keys)
        {
            ResourceOpenHandle program = await Files.CreateAndOpenAsync(bin, name, 0775, NinePConstants.OWRITE, Context(0), CancellationToken.None);
            await Files.WriteAsync(program, 0, Encoding.UTF8.GetBytes($"\0fog {name}\n"), Context(0), CancellationToken.None);
            await Files.ClunkAsync(program, Context(0), CancellationToken.None);
        }

        VProcess first = Table.CreateInitial(new NamespaceNavigator(new MountTable(), Files).Attach(root));
        RamFs environment = NewEnvironment();
        first.ProcessGroup.MountTable.Mount(environment.Root, env, MountFlags.Replace | MountFlags.Create);
        return new Process(this, first, null, environment, "*init*");
    }

    internal RamFs NewEnvironment() => new("env", "#e", User);

    internal ProgramMain? Program(string name) => programs.GetValueOrDefault(name);

    internal ResourceOperationContext Context(long pid)
        => new(new ResourceOperationId(session, (ulong)Interlocked.Increment(ref sequence)), pid, User);
}
