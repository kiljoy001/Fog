using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// The commands a kernel's /bin holds, by name.
public static class Commands
{
    public static IReadOnlyDictionary<string, ProgramMain> All { get; } = new Dictionary<string, ProgramMain>
    {
        ["auth/aescbc"] = Aescbc.MainAsync,
        ["auth/factotum"] = Factotum.MainAsync,
        ["auth/newns"] = AuthNewns.MainAsync,
        ["auth/secstore"] = Secstore.MainAsync,
        ["auth/secstored"] = Secstored.MainAsync,
        ["auth/secuser"] = Secuser.MainAsync,
        ["aux/listen"] = Listen.MainAsync,
        ["aux/listen1"] = Listen1.MainAsync,
        ["aux/trampoline"] = Trampoline.MainAsync,
        ["bind"] = Bind.MainAsync,
        ["cat"] = Cat.MainAsync,
        ["echo"] = Echo.MainAsync,
        ["mkdir"] = Mkdir.MainAsync,
        ["mntgen"] = Mntgen.MainAsync,
        ["mount"] = Mount.MainAsync,
        ["ndb/cs"] = Cs.MainAsync,
        ["ndb/csquery"] = Csquery.MainAsync,
        ["pwd"] = Pwd.MainAsync,
        ["read"] = Read.MainAsync,
        ["rm"] = Rm.MainAsync,
        ["sleep"] = Sleep.MainAsync,
        ["test"] = Test.MainAsync,
        ["tlssrv"] = Tlssrv.MainAsync,
        ["unmount"] = Unmount.MainAsync,
    };
}
