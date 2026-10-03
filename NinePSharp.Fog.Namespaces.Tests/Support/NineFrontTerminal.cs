using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace NinePSharp.Fog.Namespaces.Tests.Support;

// A 9front release ISO booted in QEMU with its console on a Unix socket. The only change to the
// ISO is plan9.ini, rewritten in place at the same length for a serial console and no prompts.
// QEMU runs under a shell that kills it when its standard input closes, so it cannot outlive the
// test process.
internal sealed class NineFrontTerminal : IAsyncDisposable
{
    internal const string Prompt = "term% ";
    private const string CdBootIni = "# config for initial cd booting\ncdboot=yes\nmouseport=ask\nmonitor=ask\nvgasize=ask\nbootfile=/amd64/9pc64\n";
    private const string SerialIni = "console=0\ncdboot=yes\nmouseport=ps2\nnobootprompt=local!/dev/sdD0/data\nuser=glenda\nbootfile=/amd64/9pc64\n";
    private readonly Process process;
    private readonly Socket console;
    private readonly string directory;
    private readonly StringBuilder output = new();
    private readonly Task reading;
    private int consumed;

    private NineFrontTerminal(Process process, Socket console, string directory)
    {
        this.process = process;
        this.console = console;
        this.directory = directory;
        reading = ReadAsync();
    }

    internal static string? Unavailable
    {
        get
        {
            string? iso = Environment.GetEnvironmentVariable("FOG_9FRONT_ISO");
            if (string.IsNullOrEmpty(iso) || !File.Exists(iso))
            {
                return "FOG_9FRONT_ISO does not name a 9front ISO";
            }

            return Environment.GetEnvironmentVariable("PATH")!.Split(':').Any(path => File.Exists(Path.Combine(path, "qemu-system-x86_64")))
                ? null
                : "qemu-system-x86_64 is not on the path";
        }
    }

    public async ValueTask DisposeAsync()
    {
        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        console.Dispose();
        await reading;
        process.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    internal static async Task<NineFrontTerminal> BootAsync()
    {
        string directory = Directory.CreateTempSubdirectory("9front-").FullName;
        string iso = Path.Combine(directory, "9front.iso");
        byte[] image = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("FOG_9FRONT_ISO")!);
        int at = image.AsSpan().IndexOf(Encoding.ASCII.GetBytes(CdBootIni));
        if (at < 0)
        {
            throw new InvalidOperationException("The ISO's /cfg/plan9.ini is not the 9front release one.");
        }

        Encoding.ASCII.GetBytes(SerialIni).CopyTo(image, at);
        await File.WriteAllBytesAsync(iso, image);
        string socket = Path.Combine(directory, "console");
        string accel = File.Exists("/dev/kvm") ? "-accel kvm -accel tcg" : "-accel tcg";
        string qemu = $"qemu-system-x86_64 {accel} -m 1024 -smp 2 -display none -monitor none -cdrom {iso} -boot d " +
            $"-nic user,model=e1000 -chardev socket,id=console,path={socket},server=on,wait=on -serial chardev:console";
        var start = new ProcessStartInfo("sh")
        {
            ArgumentList = { "-c", $"{qemu} & vm=$!; read -r _; kill $vm; wait $vm" },
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        Process process = Process.Start(start) ?? throw new InvalidOperationException("qemu did not start");
        Socket console = await ConnectAsync(socket, process);
        var terminal = new NineFrontTerminal(process, console, directory);
        await terminal.ExpectAsync(Prompt, TimeSpan.FromMinutes(3));
        return terminal;
    }

    // Runs one rc command line and returns what it printed before the next prompt.
    internal async Task<string> RunAsync(string command, TimeSpan? timeout = null)
    {
        await SendLineAsync(command);
        string printed = await ExpectAsync(Prompt, timeout ?? TimeSpan.FromSeconds(60));
        int echo = printed.IndexOf('\n', StringComparison.Ordinal);
        return echo < 0 ? string.Empty : printed[(echo + 1)..];
    }

    internal async Task SendLineAsync(string line) => await console.SendAsync(Encoding.UTF8.GetBytes(line + "\n"));

    // Waits for text the console has not yet shown past, and returns everything before it.
    internal async Task<string> ExpectAsync(string text, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            lock (output)
            {
                string seen = output.ToString().Replace("\r", string.Empty, StringComparison.Ordinal);
                int found = seen.IndexOf(text, consumed, StringComparison.Ordinal);
                if (found >= 0)
                {
                    string before = seen[consumed..found];
                    consumed = found + text.Length;
                    return before;
                }
            }

            if (reading.IsCompleted || deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"9front did not print \"{text}\"; it last printed:\n{Transcript(4000)}");
            }

            await Task.Delay(50, CancellationToken.None);
        }
    }

    // The console's last characters, for a failure message.
    internal string Transcript(int length)
    {
        lock (output)
        {
            string all = output.ToString().Replace("\r", string.Empty, StringComparison.Ordinal);
            return all[Math.Max(0, all.Length - length)..];
        }
    }

    private static async Task<Socket> ConnectAsync(string path, Process process)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (File.Exists(path))
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
                    return socket;
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException("qemu exited: " + await process.StandardError.ReadToEndAsync());
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("qemu did not open its console socket");
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = await console.ReceiveAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                lock (output)
                {
                    output.Append(Encoding.Latin1.GetString(buffer, 0, read));
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }
    }
}
