using System.Runtime.CompilerServices;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// errstr and werrstr: each process's own error string, as Plan 9 keeps it, where a library function
// leaves why it failed.
public static class Errstr
{
    private static readonly ConditionalWeakTable<Process, string> Buf = [];

    public static string Get(Process up) => Buf.TryGetValue(up, out string? e) ? e : string.Empty;

    public static void Set(Process up, string e) => Buf.AddOrUpdate(up, e);
}
