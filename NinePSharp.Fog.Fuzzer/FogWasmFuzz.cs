using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Wasm;

namespace NinePSharp.Fuzzer;

// A Preview 1 module image, read, checked, compiled and set up as exec loads it: it is admitted or
// refused with "exec header invalid", and fails no other way. Nothing admitted is run.
public static class FogWasmFuzz
{
    private static readonly Process Init = FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync().GetAwaiter().GetResult();

    public static void Run(Stream input)
    {
        byte[] buffer = new byte[65536];
        int length = input.ReadAtLeast(buffer, buffer.Length, false);
        try
        {
            Preview1Module.Load(buffer[..length], Init);
        }
        catch (SyscallException refused) when (refused.Message == "exec header invalid")
        {
        }
    }
}
