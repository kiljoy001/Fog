using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using WebAssembly;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// A core module admitted to run under WASI Preview 1. Admission checks what the engine would allow:
// a start section, which would run before the host holds the memory, and the one memory the module
// defines, which must be 32-bit with a finite maximum of at most MaxPages and not shared;
// dotnet-webassembly ignores the shared flag, so it is read from the binary. The rest holds because
// the module is set up when exec loads it: imports are bound only as Preview 1's functions with
// their WITX types, so any other import fails to bind; the exports must hold a memory named "memory"
// and a _start: () -> (). A module that cannot be set up is refused before exec commits; one loaded
// and never run is left to the collector, which frees its memory.
internal sealed class Preview1Module
{
    public const ulong MaxPages = 16384;
    private const byte MemorySection = 5;
    private const uint Shared = 2;
    private readonly Preview1Host host;
    private readonly Instance<object> instance;
    private readonly Action start;

    private Preview1Module(Preview1Host host, Instance<object> instance, Action start)
    {
        this.host = host;
        this.instance = instance;
        this.start = start;
    }

    public static Preview1Module Load(byte[] image, Process process) => TryLoad(image, process) ?? throw new SyscallException(Errors.BadExec);

    public async Task RunAsync(Process process, IReadOnlyList<string> argv)
    {
        using (instance)
        {
            host.Start(argv, await EnvironmentAsync(process));
            process.Exits(await Guest.RunAsync(start));
        }
    }

    private static Preview1Module? TryLoad(byte[] image, Process process)
    {
        try
        {
            Module module = Module.ReadFromBinary(new MemoryStream(image));
            if (Admissible(module) && !SharesMemory(image))
            {
                return Instantiate(module, image, process);
            }
        }
        catch (Exception)
        {
            // However an untrusted image fails to be read, compiled or set up, it is not a program.
        }

        return null;
    }

    private static bool Admissible(Module module)
        => module.Start is null && module.Memories is [{ ResizableLimits: { Is64Bit: false, Maximum64: <= MaxPages } }];

    private static Preview1Module Instantiate(Module module, byte[] image, Process process)
    {
        var host = new Preview1Host(process);
        var imports = new ImportDictionary();
        foreach (Import.Function function in module.Imports.OfType<Import.Function>())
        {
            imports.Add(Preview1.Module, function.Field, new FunctionImport(host.Function(function.Field)));
        }

        Instance<object> instance = Compile.FromBinary<object>(new MemoryStream(image))(imports);
        object exports = instance.Exports;
        host.Attach((UnmanagedMemory)exports.GetType().GetProperty("memory")!.GetValue(exports)!);
        return new Preview1Module(host, instance, (Action)Delegate.CreateDelegate(typeof(Action), exports, "_start"));
    }

    // The flags of the one memory the module defines; admission has already found it.
    private static bool SharesMemory(byte[] image)
    {
        int offset = 8;
        while (image[offset] != MemorySection)
        {
            offset++;
            uint size = WasmBinary.Leb128(image, ref offset);
            offset += (int)size;
        }

        offset++;
        WasmBinary.Leb128(image, ref offset);
        WasmBinary.Leb128(image, ref offset);
        return (WasmBinary.Leb128(image, ref offset) & Shared) != 0;
    }

    // Preview 1's environment is fixed when the module starts: the process's /env as it is then.
    private static async Task<IReadOnlyList<string>> EnvironmentAsync(Process process)
    {
        int directory = await process.OpenAsync("/env", NinePConstants.OREAD);
        var environment = new List<string>();
        foreach (Stat entry in await process.DirReadAsync(directory))
        {
            int fd = await process.OpenAsync($"/env/{entry.Name}", NinePConstants.OREAD);
            environment.Add($"{entry.Name}={Encoding.UTF8.GetString((await process.ReadAsync(fd, (int)entry.Length)).Span)}");
            await process.CloseAsync(fd);
        }

        await process.CloseAsync(directory);
        return environment;
    }
}
