using NinePSharp.Fog.Kernel;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// The guest's code runs on a thread of its own: an import cannot suspend the compiled WASM stack, so
// each kernel call blocks that thread and only that one. What ends the guest becomes its status.
internal static class Guest
{
    public static Task<string> RunAsync(Action start)
    {
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() => ended.SetResult(Run(start))).Start();
        return ended.Task;
    }

    private static string Run(Action start)
    {
        try
        {
            start();
            return string.Empty;
        }
        catch (ProcExitException exit)
        {
            return exit.Code == 0 ? string.Empty : $"wasm-exit {exit.Code}";
        }
        catch (ExitException exit)
        {
            // A kernel call that ends the process, such as a write on a closed pipe, ends it as it
            // would any process.
            return exit.Message;
        }
        catch (Exception trap)
        {
            return "wasm-trap: " + Trap(trap);
        }
    }

    // Traps by the names WASI runtimes give them; any other by the engine's own description.
    private static string Trap(Exception trap) => trap switch
    {
        UnreachableException => "unreachable",
        MemoryAccessOutOfRangeException => "out of bounds memory access",
        DivideByZeroException => "integer divide by zero",
        OverflowException => "integer overflow",
        IndexOutOfRangeException => "out of bounds table access",
        NullReferenceException => "uninitialized element",
        _ => trap.Message,
    };
}
