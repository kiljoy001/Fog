using System.Security.Cryptography;
using System.Text;
using NinePSharp.Fog.Namespaces.Tests.Support;

namespace NinePSharp.Fog.Rc.Tests.Support;

// One 9front terminal for the test run, booted when a scenario first asks it something it has not
// answered before. A run without a 9front ISO or qemu-system-x86_64 fails rather than leaving the
// comparison out.
internal static class NineFrontOracle
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static NineFrontTerminal? terminal;

    // What 9front prints for the last of the commands, run in order on its terminal.
    internal static async Task<string> AskAsync(params string[] commands)
    {
        if (NineFrontTerminal.Unavailable is string reason)
        {
            throw new InvalidOperationException(reason);
        }

        // An answer depends only on the release and the commands, so each test process, and each
        // mutant's, reads it from the release's cache instead of booting a VM to ask again.
        string file = Path.Combine(NineFrontTerminal.Cache, NineFrontTerminal.Release, Key(commands));
        if (File.Exists(file))
        {
            return await File.ReadAllTextAsync(file);
        }

        string answer = string.Empty;
        foreach (string command in commands)
        {
            answer = await (await TerminalAsync()).RunAsync(command);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string partial = $"{file}.{Environment.ProcessId}";
        await File.WriteAllTextAsync(partial, answer);
        File.Move(partial, file, overwrite: true);
        return answer;
    }

    internal static async Task StopAsync()
    {
        if (terminal is not null)
        {
            await terminal.DisposeAsync();
            terminal = null;
        }
    }

    private static async Task<NineFrontTerminal> TerminalAsync()
    {
        await Gate.WaitAsync();
        try
        {
            return terminal ??= await NineFrontTerminal.BootAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Key(string[] commands)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', commands))));
}
