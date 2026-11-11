using System.Text;
using System.Threading.Channels;

namespace NinePSharp.Fog.Client;

// fog's Local on a console: Ctrl-C as an interrupt, the terminal for /dev/cons and the password,
// read from /dev/tty when standard input is not the terminal, its typing unechoed while it is raw,
// and the drives on Windows.
public static class ConsoleLocal
{
    public static Local Create(IConsole console)
    {
        var interrupts = Channel.CreateUnbounded<string>();
        console.OnCancelKeyPress(() => interrupts.Writer.TryWrite("interrupt"));

        // The terminal, opened once it is wanted.
        var terminal = new Lazy<TextReader>(() => !console.IsInputRedirected || console.IsWindows || !console.HasTty ? console.In : console.OpenTty());
        bool raw = false;
        return new Local
        {
            Input = console.OpenStandardInput(),
            Output = console.OpenStandardOutput(),
            Error = console.OpenStandardError(),
            Directory = console.CurrentDirectory,
            Environment = console.Environment,
            LoginName = console.UserName,
            ReadPassword = (prompt, ct) => ReadPasswordAsync(console, prompt, terminal, ct),
            ReadConsole = async ct => await (raw ? ReadUnechoedAsync(console, terminal, ct) : Terminal(terminal).ReadLineAsync(ct)) is { } line ? line + "\n" : null,
            Raw = on => raw = on,
            WriteConsole = async (data, ct) =>
            {
                using Stream output = console.OpenStandardOutput();
                await output.WriteAsync(data, ct);
            },
            Interrupts = interrupts.Reader,
            Drives = console.IsWindows ? console.Drives() : new Dictionary<string, string>(),
        };
    }

    // The password typed after the prompt, not echoed, and the prompt ended with a newline.
    private static async ValueTask<string?> ReadPasswordAsync(IConsole console, string prompt, Lazy<TextReader> terminal, CancellationToken ct)
    {
        console.WriteError(prompt);
        try
        {
            return await ReadUnechoedAsync(console, terminal, ct);
        }
        finally
        {
            console.WriteErrorLine();
        }
    }

    // A line typed and not echoed: read key by key at a terminal, and from /dev/tty with its echo
    // turned off when standard input is not one.
    private static async ValueTask<string?> ReadUnechoedAsync(IConsole console, Lazy<TextReader> terminal, CancellationToken ct)
    {
        if (!console.IsInputRedirected)
        {
            var typed = new StringBuilder();
            for (ConsoleKeyInfo key; (key = console.ReadKey()).Key != ConsoleKey.Enter;)
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

        Stty(console, "-echo");
        try
        {
            return await Terminal(terminal).ReadLineAsync(ct);
        }
        finally
        {
            Stty(console, "echo");
        }
    }

    // stty on the terminal, where there is one to set.
    private static void Stty(IConsole console, string mode)
    {
        if (console.IsWindows || !console.HasTty)
        {
            return;
        }

        console.Stty(mode);
    }

    // The terminal, or, where there is none to open, an empty one.
    private static TextReader Terminal(Lazy<TextReader> terminal)
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
}
