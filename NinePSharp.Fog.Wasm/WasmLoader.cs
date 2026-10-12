using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Wasm;

// exec's loader for WASM: a file that begins with the WASM magic is a module, which the engine refuses
// unless the version that follows is a core module's.
public static class WasmLoader
{
    // The largest image exec reads.
    public const int ImageLimit = 64 << 20;

    private static readonly byte[] Magic = [0x00, 0x61, 0x73, 0x6d];

    public static async ValueTask<ProgramMain?> LoadAsync(Process process, string path, ReadOnlyMemory<byte> header)
    {
        if (!header.Span.StartsWith(Magic))
        {
            return null;
        }

        return Preview1Module.Admit(await process.ReadImageAsync(path, ImageLimit)).RunAsync;
    }
}
