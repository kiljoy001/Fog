using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// The commands a kernel's /bin holds, by name.
public static class Commands
{
    public static IReadOnlyDictionary<string, ProgramMain> All { get; } = new Dictionary<string, ProgramMain>
    {
        ["auth/newns"] = AuthNewns.MainAsync,
        ["aux/listen1"] = Listen1.MainAsync,
        ["aux/trampoline"] = Trampoline.MainAsync,
        ["bind"] = Bind.MainAsync,
        ["cat"] = Cat.MainAsync,
        ["echo"] = Echo.MainAsync,
        ["mkdir"] = Mkdir.MainAsync,
        ["mount"] = Mount.MainAsync,
        ["read"] = Read.MainAsync,
        ["rm"] = Rm.MainAsync,
        ["sleep"] = Sleep.MainAsync,
        ["test"] = Test.MainAsync,
        ["unmount"] = Unmount.MainAsync,
    };
}
