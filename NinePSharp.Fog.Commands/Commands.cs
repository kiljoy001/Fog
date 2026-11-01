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
        ["basename"] = Basename.MainAsync,
        ["bind"] = Bind.MainAsync,
        ["cat"] = Cat.MainAsync,
        ["chmod"] = Chmod.MainAsync,
        ["cleanname"] = Cleanname.MainAsync,
        ["cmp"] = Cmp.MainAsync,
        ["cp"] = Cp.MainAsync,
        ["du"] = Du.MainAsync,
        ["echo"] = Echo.MainAsync,
        ["ls"] = Ls.MainAsync,
        ["mkdir"] = Mkdir.MainAsync,
        ["mntgen"] = Mntgen.MainAsync,
        ["mount"] = Mount.MainAsync,
        ["mv"] = Mv.MainAsync,
        ["ndb/cs"] = Cs.MainAsync,
        ["ndb/csquery"] = Csquery.MainAsync,
        ["pwd"] = Pwd.MainAsync,
        ["read"] = Read.MainAsync,
        ["rm"] = Rm.MainAsync,
        ["seq"] = Seq.MainAsync,
        ["sleep"] = Sleep.MainAsync,
        ["tee"] = Tee.MainAsync,
        ["test"] = Test.MainAsync,
        ["touch"] = Touch.MainAsync,
        ["tail"] = Tail.MainAsync,
        ["tr"] = Tr.MainAsync,
        ["tlssrv"] = Tlssrv.MainAsync,
        ["uniq"] = Uniq.MainAsync,
        ["unmount"] = Unmount.MainAsync,
        ["wc"] = Wc.MainAsync,
        ["xd"] = Xd.MainAsync,
    };
}
