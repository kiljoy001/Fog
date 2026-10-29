using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace NinePSharp.Fog.Namespaces.Tests.Support;

// A 9front release ISO booted in QEMU with its console on a Unix socket. The only change to the
// ISO is plan9.ini, rewritten in place at the same length for a serial console and no prompts; the
// changed ISO is kept with the release's answers and shared by every boot, as QEMU only reads it.
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

    // The 9front amd64 ISO: FOG_9FRONT_ISO, or ~/9front/current.iso, where the gate's
    // refresh-9front keeps the newest release.
    internal static string Iso
        => Environment.GetEnvironmentVariable("FOG_9FRONT_ISO") is { Length: > 0 } iso
            ? iso
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "9front", "current.iso");

    // Where what is kept of each release is: FOG_9FRONT_ANSWERS, or ~/.cache/fog/9front.
    internal static string Cache
        => Environment.GetEnvironmentVariable("FOG_9FRONT_ANSWERS") is { Length: > 0 } cache
            ? cache
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "fog", "9front");

    // The release of the ISO, by the name, size and time of the file a link leads to.
    internal static string Release
    {
        get
        {
            var iso = new FileInfo(Iso);
            if (iso.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
            {
                iso = target;
            }

            return $"{iso.Name}-{iso.Length}-{iso.LastWriteTimeUtc.Ticks}";
        }
    }

    // Why no 9front terminal can be booted, or null when one can.
    internal static string? Unavailable
    {
        get
        {
            if (!File.Exists(Iso))
            {
                return $"no 9front ISO at {Iso}: set FOG_9FRONT_ISO or link ~/9front/current.iso";
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

    // With each forward, a port of the host's loopback reaches the port of 9front's given with it.
    internal static async Task<NineFrontTerminal> BootAsync(params (int Host, int Guest)[] forwards)
    {
        string iso = await SerialIsoAsync();
        string directory = Directory.CreateTempSubdirectory("9front-").FullName;
        string socket = Path.Combine(directory, "console");
        string accel = File.Exists("/dev/kvm") ? "-accel kvm -accel tcg" : "-accel tcg";
        string qemu = $"qemu-system-x86_64 {accel} -m 1024 -smp 2 -display none -monitor none -cdrom {iso} -boot d " +
            $"-nic user,model=e1000{string.Concat(forwards.Select(f => $",hostfwd=tcp:127.0.0.1:{f.Host}-:{f.Guest}"))} -chardev socket,id=console,path={socket},server=on,wait=on -serial chardev:console";
        var start = new ProcessStartInfo("sh")
        {
            ArgumentList = { "-c", $"{qemu} & vm=$!; read -r _; kill $vm; wait $vm" },
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        Process process = Process.Start(start) ?? throw new InvalidOperationException("qemu did not start");
        NineFrontTerminal terminal;
        try
        {
            terminal = new NineFrontTerminal(process, await ConnectAsync(socket, process), directory);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            Directory.Delete(directory, recursive: true);
            throw;
        }

        try
        {
            await terminal.ExpectAsync(Prompt, TimeSpan.FromMinutes(3));
        }
        catch
        {
            await terminal.DisposeAsync();
            throw;
        }

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

    // The serial line has no flow control, so text sent faster than 9front echoes it loses bytes:
    // each line waits for its echo before the next is sent.
    internal async Task SendLineAsync(string text)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            await console.SendAsync(Encoding.UTF8.GetBytes(lines[i] + "\n"));
            if (i < lines.Length - 1)
            {
                await ExpectAsync(lines[i] + "\n", TimeSpan.FromSeconds(10));
            }
        }
    }

    // Keys typed as they are, with no newline and no wait for an echo, as Del is.
    internal async Task TypeAsync(string keys) => await console.SendAsync(Encoding.UTF8.GetBytes(keys));

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

    // The release's ISO with plan9.ini set for the serial console, made once per release.
    private static async Task<string> SerialIsoAsync()
    {
        string iso = Path.Combine(Cache, Release, "9front.iso");
        if (File.Exists(iso))
        {
            return iso;
        }

        byte[] image = await File.ReadAllBytesAsync(Iso);
        int at = image.AsSpan().IndexOf(Encoding.ASCII.GetBytes(CdBootIni));
        if (at < 0)
        {
            throw new InvalidOperationException("The ISO's /cfg/plan9.ini is not the 9front release one.");
        }

        Encoding.ASCII.GetBytes(SerialIni).CopyTo(image, at);
        Directory.CreateDirectory(Path.GetDirectoryName(iso)!);
        string partial = $"{iso}.{Environment.ProcessId}";
        await File.WriteAllBytesAsync(partial, image);
        File.Move(partial, iso, overwrite: true);
        return iso;
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
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        Decoder decoder = Encoding.UTF8.GetDecoder();
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
                    output.Append(chars, 0, decoder.GetChars(buffer, 0, read, chars, 0));
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }
    }
}
