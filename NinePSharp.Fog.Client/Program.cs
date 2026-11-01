using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using NinePSharp.Fog.Client;

// fog on the console it is run from: its streams, directory and environment, the terminal for
// /dev/cons and the password, Ctrl-C as an interrupt, and the drives on Windows.
var interrupts = Channel.CreateUnbounded<string>();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    interrupts.Writer.TryWrite("interrupt");
};

// The terminal, opened once it is wanted: /dev/tty when standard input is not the terminal.
var terminal = new Lazy<TextReader>(() => !Console.IsInputRedirected || OperatingSystem.IsWindows() || !File.Exists("/dev/tty")
    ? Console.In
    : new StreamReader(File.OpenRead("/dev/tty"), Encoding.UTF8));

var local = new Local
{
    Input = Console.OpenStandardInput(),
    Output = Console.OpenStandardOutput(),
    Error = Console.OpenStandardError(),
    Directory = Environment.CurrentDirectory,
    Environment = Environment.GetEnvironmentVariable,
    LoginName = Environment.UserName,
    ReadPassword = (prompt, ct) => ReadPasswordAsync(prompt, terminal, ct),
    ReadConsole = async ct => await Terminal(terminal).ReadLineAsync(ct) is { } line ? line + "\n" : null,
    WriteConsole = async (data, ct) =>
    {
        using Stream output = Console.OpenStandardOutput();
        await output.WriteAsync(data, ct);
    },
    Interrupts = interrupts.Reader,
    Drives = OperatingSystem.IsWindows()
        ? DriveInfo.GetDrives().Where(d => d.IsReady).ToDictionary(d => d.Name.TrimEnd('\\'), d => d.RootDirectory.FullName)
        : new Dictionary<string, string>(),
};

return await Rcpu.RunAsync(args, local, CancellationToken.None);

// The password typed after the prompt, not echoed: read key by key at a terminal, and from
// /dev/tty with its echo turned off when standard input is not one.
static async ValueTask<string?> ReadPasswordAsync(string prompt, Lazy<TextReader> terminal, CancellationToken ct)
{
    Console.Error.Write(prompt);
    try
    {
        if (!Console.IsInputRedirected)
        {
            var typed = new StringBuilder();
            for (ConsoleKeyInfo key; (key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter;)
            {
                if (key.Key == ConsoleKey.Backspace)
                {
                    typed.Length = Math.Max(0, typed.Length - 1);
                }
                else
                {
                    typed.Append(key.KeyChar);
                }
            }

            return typed.ToString();
        }

        Stty("-echo");
        try
        {
            return await Terminal(terminal).ReadLineAsync(ct);
        }
        finally
        {
            Stty("echo");
        }
    }
    finally
    {
        Console.Error.WriteLine();
    }
}

// stty on the terminal, where there is one to set; sh gives it /dev/tty as GNU's and BSD's stty
// both take it.
static void Stty(string mode)
{
    if (OperatingSystem.IsWindows() || !File.Exists("/dev/tty"))
    {
        return;
    }

    try
    {
        using var stty = Process.Start(new ProcessStartInfo("sh", ["-c", $"stty {mode} </dev/tty"]) { UseShellExecute = false });
        stty?.WaitForExit();
    }
    catch (System.ComponentModel.Win32Exception)
    {
    }
}

// The terminal, or, where there is none to open, an empty one.
static TextReader Terminal(Lazy<TextReader> terminal)
{
    try
    {
        return terminal.Value;
    }
    catch (IOException)
    {
        return TextReader.Null;
    }
}
