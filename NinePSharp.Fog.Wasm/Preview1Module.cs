using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using WebAssembly;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// A core module admitted to run under WASI Preview 1: one memory of its own with a finite maximum
// of at most MaxPages, exported as "memory"; no start section, which would run imports before the
// host holds the memory; an exported _start: () -> (); and imports only of Preview 1 functions with
// their exact types. dotnet-webassembly ignores a memory's shared flag, so it is read from the binary.
internal sealed class Preview1Module
{
    public const ulong MaxPages = 16384;
    private const byte MemorySection = 5;
    private const uint Shared = 2;
    private readonly Module module;
    private readonly InstanceCreator<object> creator;

    private Preview1Module(Module module, InstanceCreator<object> creator)
    {
        this.module = module;
        this.creator = creator;
    }

    public static Preview1Module Admit(byte[] image)
    {
        try
        {
            Module module = Module.ReadFromBinary(new MemoryStream(image));
            if (!Admissible(module) || SharesMemory(image))
            {
                throw new SyscallException(Errors.BadExec);
            }

            return new Preview1Module(module, Compile.FromBinary<object>(new MemoryStream(image)));
        }
        catch (Exception refused) when (refused is ModuleLoadException or CompilerException)
        {
            throw new SyscallException(Errors.BadExec);
        }
    }

    public async Task RunAsync(Process process, IReadOnlyList<string> argv)
    {
        var host = new Preview1Host(process, argv, await EnvironmentAsync(process));
        var imports = new ImportDictionary();
        foreach (Import.Function function in module.Imports.Cast<Import.Function>())
        {
            imports.Add(Preview1.Module, function.Field, new FunctionImport(host.Function(function.Field)));
        }

        using Instance<object> instance = creator(imports);
        object exports = instance.Exports;
        host.Attach((UnmanagedMemory)exports.GetType().GetProperty("memory")!.GetValue(exports)!);
        process.Exits(await Guest.RunAsync((Action)Delegate.CreateDelegate(typeof(Action), exports, "_start")));
    }

    private static bool Admissible(Module module)
    {
        int imported = module.Imports.Count;
        return module.Start is null
            && module.Memories is [{ ResizableLimits: { Is64Bit: false, Maximum64: <= MaxPages } }]
            && module.Imports.All(import => import is Import.Function function
                && function.Module == Preview1.Module
                && Preview1.Functions.TryGetValue(function.Field, out string? signature)
                && Preview1.Matches(signature, module.Types[(int)function.TypeIndex]))
            && module.Exports.Any(export => export is { Name: "memory", Kind: ExternalKind.Memory })
            && module.Exports.FirstOrDefault(export => export is { Name: "_start", Kind: ExternalKind.Function }) is { } start
            && module.Functions.ElementAtOrDefault((int)start.Index - imported) is { } entry
            && module.Types[(int)entry.Type] is { Parameters.Count: 0, Returns.Count: 0 };
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
