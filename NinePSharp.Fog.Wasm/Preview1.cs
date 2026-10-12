using System.Linq.Expressions;
using System.Reflection.Emit;

namespace NinePSharp.Fog.Wasm;

// The functions of wasi_snapshot_preview1 that Fog does not provide, with their parameters as the
// WITX at a2b96e81 lowers them to core types, i for i32 and I for i64. Each returns its errno, which
// is NOSYS. A module importing a name neither here nor among the functions Preview1Host provides, or
// one of them with another type, cannot be set up.
internal static class Preview1
{
    public const string Module = "wasi_snapshot_preview1";

    public static readonly IReadOnlyDictionary<string, string> Unprovided = new Dictionary<string, string>
    {
        ["clock_res_get"] = "ii",
        ["clock_time_get"] = "iIi",
        ["fd_advise"] = "iIIi",
        ["fd_allocate"] = "iII",
        ["fd_datasync"] = "i",
        ["fd_fdstat_get"] = "ii",
        ["fd_fdstat_set_flags"] = "ii",
        ["fd_fdstat_set_rights"] = "iII",
        ["fd_filestat_get"] = "ii",
        ["fd_filestat_set_size"] = "iI",
        ["fd_filestat_set_times"] = "iIIi",
        ["fd_prestat_get"] = "ii",
        ["fd_prestat_dir_name"] = "iii",
        ["fd_readdir"] = "iiiIi",
        ["fd_sync"] = "i",
        ["path_create_directory"] = "iii",
        ["path_filestat_get"] = "iiiii",
        ["path_filestat_set_times"] = "iiiiIIi",
        ["path_link"] = "iiiiiii",
        ["path_open"] = "iiiiiIIii",
        ["path_readlink"] = "iiiiii",
        ["path_remove_directory"] = "iii",
        ["path_rename"] = "iiiiii",
        ["path_symlink"] = "iiiii",
        ["path_unlink_file"] = "iii",
        ["poll_oneoff"] = "iiii",
        ["proc_raise"] = "i",
        ["sched_yield"] = string.Empty,
        ["random_get"] = "ii",
        ["sock_accept"] = "iii",
        ["sock_recv"] = "iiiiii",
        ["sock_send"] = "iiiii",
        ["sock_shutdown"] = "ii",
    };

    // A function Fog does not provide: it takes its arguments and returns NOSYS. The engine wants a
    // method of exactly the import's parameters, which a compiled lambda's closure would not be.
    public static Delegate Unsupported(string signature)
    {
        Type[] parameters = [.. signature.Select(c => c == 'I' ? typeof(long) : typeof(int))];
        var stub = new DynamicMethod(nameof(Unsupported), typeof(int), parameters, typeof(Preview1).Module);
        ILGenerator il = stub.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, (int)Errno.Nosys);
        il.Emit(OpCodes.Ret);
        return stub.CreateDelegate(Expression.GetFuncType([.. parameters, typeof(int)]));
    }
}
