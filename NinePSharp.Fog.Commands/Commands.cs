using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// The commands a kernel's /bin holds, by name.
public static class Commands
{
    public static IReadOnlyDictionary<string, ProgramMain> All { get; } = new Dictionary<string, ProgramMain>
    {
        ["bind"] = Bind.MainAsync,
        ["cat"] = Cat.MainAsync,
        ["echo"] = Echo.MainAsync,
        ["mkdir"] = Mkdir.MainAsync,
    };
}
