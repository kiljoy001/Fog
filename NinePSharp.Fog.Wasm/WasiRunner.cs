using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Wasm;

// fog-wasi: runs a Preview 1 module from the host, taking the arguments Wasmtime takes for what
// wasi-testsuite asks of a runtime. The module runs as a child of a kernel of its own, on a copy of
// the host directory that is written back once it ends. Each step of the copy that opens a file runs
// as a process of its own, whose descriptors close when it ends. The host's streams are written
// unbuffered.
public static class WasiRunner
{
    public const int Misused = 2;
    public const int Trapped = 134;
    public const string Usage = "usage: fog-wasi [--env NAME=VALUE]... [--dir HOST::/] module.wasm [arg]...\n";

    private const string Image = "/bin/module";
    private const string Copy = "/tmp/host";
    private const uint Executable = 0b111_111_101;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, Stream stdin, Stream stdout, Stream stderr)
    {
        var environment = new List<string>();
        string? host = null;
        int at = 0;
        for (; at + 1 < args.Count && args[at].StartsWith("--", StringComparison.Ordinal); at += 2)
        {
            string value = args[at + 1];
            if (args[at] == "--env" && value.IndexOf('=') > 0)
            {
                environment.Add(value);
            }
            else if (args[at] == "--dir" && host is null && value.EndsWith("::/", StringComparison.Ordinal))
            {
                host = value[..^3];
            }
            else
            {
                return await FailAsync(stderr, Usage, Misused);
            }
        }

        if (at == args.Count || args[at].StartsWith("--", StringComparison.Ordinal))
        {
            return await FailAsync(stderr, Usage, Misused);
        }

        TimeProvider clock = TimeProvider.System;
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), clock: clock, loaders: [new WasmLoader(clock).LoadAsync]).BootAsync();
        await WriteAsync(init, Image, await File.ReadAllBytesAsync(args[at]), Executable);
        if (host is not null)
        {
            await CopyInAsync(init, new DirectoryInfo(host), Copy);
        }

        var (input, toInput) = await init.PipeAsync();
        var (output, fromOutput) = await init.PipeAsync();
        var (error, fromError) = await init.PipeAsync();
        string[] argv = [.. args.Skip(at)];
        long child = init.Fork(RforkFlags.Fdg | RforkFlags.Nameg | RforkFlags.Cenvg, async process =>
        {
            await StdioAsync(process, input, output, error);
            foreach (string variable in environment)
            {
                int equals = variable.IndexOf('=');
                await WriteAsync(process, "/env/" + variable[..equals], Encoding.UTF8.GetBytes(variable[(equals + 1)..]), 0b110_110_100);
            }

            if (host is not null)
            {
                await process.BindAsync(Copy, "/", MountFlags.Before | MountFlags.Create);
                await process.RenumberAsync(await process.OpenAsync("/", NinePConstants.OREAD), 3);
            }

            await process.ExecAsync(Image, argv);
        });

        foreach (int end in new[] { input, output, error })
        {
            await init.CloseAsync(end);
        }

        _ = FeedAsync(init, toInput, stdin);
        Task<Waitmsg> waiting = init.WaitAsync();
        await Task.WhenAll(PumpAsync(init, fromOutput, stdout), PumpAsync(init, fromError, stderr), waiting);
        Waitmsg waited = await waiting;
        if (host is not null)
        {
            await CopyOutAsync(init, Copy, new DirectoryInfo(host));
        }

        string prefix = $" {child}: ";
        string status = waited.Message.Length == 0 ? string.Empty : waited.Message[(waited.Message.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..];
        if (status.Length == 0)
        {
            return 0;
        }

        if (status.StartsWith("wasm-exit ", StringComparison.Ordinal))
        {
            return int.Parse(status["wasm-exit ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        }

        return await FailAsync(stderr, status + "\n", status.StartsWith("wasm-trap: ", StringComparison.Ordinal) ? Trapped : 1);
    }

    private static async Task<int> FailAsync(Stream stderr, string message, int code)
    {
        await stderr.WriteAsync(Encoding.UTF8.GetBytes(message));
        return code;
    }

    // Leaves the child holding the pipes' ends as descriptors 0, 1 and 2, and nothing else.
    private static async Task StdioAsync(Process process, int input, int output, int error)
    {
        const int Aside = 10;
        int[] ends = [input, output, error];
        for (int i = 0; i < ends.Length; i++)
        {
            await process.RenumberAsync(ends[i], Aside + i);
        }

        int listing = await process.OpenAsync("/fd", NinePConstants.OREAD);
        IEnumerable<int> open = (await process.DirReadAsync(listing)).Select(entry => entry.Name).Where(name => !name.EndsWith("ctl", StringComparison.Ordinal)).Select(int.Parse);
        foreach (int fd in open.Where(fd => fd is < Aside or > Aside + 2))
        {
            await process.CloseAsync(fd);
        }

        for (int i = 0; i < ends.Length; i++)
        {
            await process.RenumberAsync(Aside + i, i);
        }
    }

    private static async Task PumpAsync(Process process, int from, Stream to)
    {
        for (ReadOnlyMemory<byte> block; !(block = await process.ReadAsync(from, 8192)).IsEmpty;)
        {
            await to.WriteAsync(block);
        }
    }

    // Once the module has gone, its stdin has no reader, and what is left of the host's is dropped.
    private static async Task FeedAsync(Process process, int to, Stream from)
    {
        byte[] buffer = new byte[8192];
        try
        {
            for (int read; (read = await from.ReadAsync(buffer)) != 0;)
            {
                await process.WriteAsync(to, buffer.AsMemory(0, read));
            }
        }
        catch (SyscallException)
        {
        }

        await process.CloseAsync(to);
    }

    // The step's process is waited for before its work is looked at, so it has ended, and how its work
    // went, result or exception, is the step's.
    private static async Task<T> AloneAsync<T>(Process parent, Func<Process, Task<T>> body)
    {
        Task<T>? work = null;
        parent.Fork(RforkFlags.Cfdg, process => work = body(process));
        await parent.WaitAsync();
        return await work!;
    }

    private static Task<int> WriteAsync(Process parent, string path, byte[] contents, uint permissions)
        => AloneAsync(parent, async process => await process.WriteAsync(await process.CreateAsync(path, NinePConstants.OWRITE, permissions), contents));

    private static Task<byte[]> ReadAsync(Process parent, string path) => AloneAsync(parent, async process =>
    {
        int fd = await process.OpenAsync(path, NinePConstants.OREAD);
        var contents = new MemoryStream();
        for (ReadOnlyMemory<byte> block; !(block = await process.ReadAsync(fd, 8192)).IsEmpty;)
        {
            contents.Write(block.Span);
        }

        return contents.ToArray();
    });

    private static uint Permissions(FileSystemInfo entry) => (uint)entry.UnixFileMode & 0b111_111_111;

    // Symbolic links have no Fog counterpart, so they are left out of the copy and left alone on the host.
    private static async Task CopyInAsync(Process process, DirectoryInfo from, string to)
    {
        await AloneAsync(process, async alone => await alone.CreateAsync(to, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | Permissions(from)));
        foreach (FileSystemInfo entry in from.EnumerateFileSystemInfos().Where(entry => entry.LinkTarget is null))
        {
            string path = $"{to}/{entry.Name}";
            if (entry is DirectoryInfo directory)
            {
                await CopyInAsync(process, directory, path);
            }
            else
            {
                await WriteAsync(process, path, await File.ReadAllBytesAsync(entry.FullName), Permissions(entry));
            }
        }
    }

    private static async Task CopyOutAsync(Process process, string from, DirectoryInfo to)
    {
        IReadOnlyList<Stat> entries = await AloneAsync(process, async alone => await alone.DirReadAsync(await alone.OpenAsync(from, NinePConstants.OREAD)));
        var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, FileSystemInfo> present = to.EnumerateFileSystemInfos().ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        foreach (FileSystemInfo stale in present.Values.Where(entry => entry.LinkTarget is null && !names.Contains(entry.Name)))
        {
            Delete(stale);
        }

        foreach (Stat entry in entries)
        {
            string path = Path.Combine(to.FullName, entry.Name);
            bool directory = HeldDirectories.IsDirectory(entry);
            if (present.TryGetValue(entry.Name, out FileSystemInfo? existing) && (existing.LinkTarget is not null || existing is DirectoryInfo != directory))
            {
                Delete(existing);
            }

            if (directory)
            {
                await CopyOutAsync(process, $"{from}/{entry.Name}", Directory.CreateDirectory(path));
            }
            else
            {
                byte[] contents = await ReadAsync(process, $"{from}/{entry.Name}");
                if (!File.Exists(path) || !(await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(contents))
                {
                    await File.WriteAllBytesAsync(path, contents);
                }
            }
        }
    }

    private static void Delete(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo directory && entry.LinkTarget is null)
        {
            directory.Delete(true);
        }
        else
        {
            entry.Delete();
        }
    }
}
