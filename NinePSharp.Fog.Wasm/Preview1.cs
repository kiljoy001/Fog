using System.Linq.Expressions;
using System.Reflection.Emit;

namespace NinePSharp.Fog.Wasm;

// The functions of wasi_snapshot_preview1 that Fog does not provide, with how many i32 parameters the
// WITX at a2b96e81 lowers each to. Each returns its errno, which is NOSYS. A module importing a name neither here nor among the functions Preview1Host provides, or
// one of them with another type, cannot be set up.
internal static class Preview1
{
    public const string Module = "wasi_snapshot_preview1";

    public static readonly IReadOnlyDictionary<string, int> Unprovided = new Dictionary<string, int>
    {
        ["fd_datasync"] = 1,
        ["fd_sync"] = 1,
        ["poll_oneoff"] = 4,
        ["proc_raise"] = 1,
    };

    // A function Fog does not provide: it takes its arguments and returns NOSYS. The engine wants a
    // method of exactly the import's parameters, which a compiled lambda's closure would not be.
    public static Delegate Unsupported(int count)
    {
        Type[] parameters = [.. Enumerable.Repeat(typeof(int), count)];
        var stub = new DynamicMethod(nameof(Unsupported), typeof(int), parameters, typeof(Preview1).Module);
        ILGenerator il = stub.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, (int)Errno.Nosys);
        il.Emit(OpCodes.Ret);
        return stub.CreateDelegate(Expression.GetFuncType([.. parameters, typeof(int)]));
    }
}
