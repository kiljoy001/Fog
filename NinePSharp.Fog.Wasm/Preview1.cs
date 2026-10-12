using System.Linq.Expressions;
using System.Reflection.Emit;
using WebAssembly;

namespace NinePSharp.Fog.Wasm;

// The functions of wasi_snapshot_preview1, as the WITX at a2b96e81 lowers them to core types: i is
// i32 and I is i64, parameters before the colon and results after it. Every function but proc_exit
// returns its errno.
internal static class Preview1
{
    public const string Module = "wasi_snapshot_preview1";

    public static readonly IReadOnlyDictionary<string, string> Functions = new Dictionary<string, string>
    {
        ["args_get"] = "ii:i",
        ["args_sizes_get"] = "ii:i",
        ["environ_get"] = "ii:i",
        ["environ_sizes_get"] = "ii:i",
        ["clock_res_get"] = "ii:i",
        ["clock_time_get"] = "iIi:i",
        ["fd_advise"] = "iIIi:i",
        ["fd_allocate"] = "iII:i",
        ["fd_close"] = "i:i",
        ["fd_datasync"] = "i:i",
        ["fd_fdstat_get"] = "ii:i",
        ["fd_fdstat_set_flags"] = "ii:i",
        ["fd_fdstat_set_rights"] = "iII:i",
        ["fd_filestat_get"] = "ii:i",
        ["fd_filestat_set_size"] = "iI:i",
        ["fd_filestat_set_times"] = "iIIi:i",
        ["fd_pread"] = "iiiIi:i",
        ["fd_prestat_get"] = "ii:i",
        ["fd_prestat_dir_name"] = "iii:i",
        ["fd_pwrite"] = "iiiIi:i",
        ["fd_read"] = "iiii:i",
        ["fd_readdir"] = "iiiIi:i",
        ["fd_renumber"] = "ii:i",
        ["fd_seek"] = "iIii:i",
        ["fd_sync"] = "i:i",
        ["fd_tell"] = "ii:i",
        ["fd_write"] = "iiii:i",
        ["path_create_directory"] = "iii:i",
        ["path_filestat_get"] = "iiiii:i",
        ["path_filestat_set_times"] = "iiiiIIi:i",
        ["path_link"] = "iiiiiii:i",
        ["path_open"] = "iiiiiIIii:i",
        ["path_readlink"] = "iiiiii:i",
        ["path_remove_directory"] = "iii:i",
        ["path_rename"] = "iiiiii:i",
        ["path_symlink"] = "iiiii:i",
        ["path_unlink_file"] = "iii:i",
        ["poll_oneoff"] = "iiii:i",
        ["proc_exit"] = "i:",
        ["proc_raise"] = "i:i",
        ["sched_yield"] = ":i",
        ["random_get"] = "ii:i",
        ["sock_accept"] = "iii:i",
        ["sock_recv"] = "iiiiii:i",
        ["sock_send"] = "iiiii:i",
        ["sock_shutdown"] = "ii:i",
    };

    public static bool Matches(string signature, WebAssemblyType type)
    {
        string[] parts = signature.Split(':');
        return type.Parameters.SequenceEqual(parts[0].Select(ValueType)) && type.Returns.SequenceEqual(parts[1].Select(ValueType));
    }

    // A function Fog does not provide: it takes its arguments and returns NOSYS. The engine wants a
    // method of exactly the import's parameters, which a compiled lambda's closure would not be.
    public static Delegate Unsupported(string signature)
    {
        Type[] parameters = [.. signature.Split(':')[0].Select(c => c == 'I' ? typeof(long) : typeof(int))];
        var stub = new DynamicMethod(nameof(Unsupported), typeof(int), parameters, typeof(Preview1).Module);
        ILGenerator il = stub.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, (int)Errno.Nosys);
        il.Emit(OpCodes.Ret);
        return stub.CreateDelegate(Expression.GetFuncType([.. parameters, typeof(int)]));
    }

    private static WebAssemblyValueType ValueType(char c) => c == 'I' ? WebAssemblyValueType.Int64 : WebAssemblyValueType.Int32;
}
