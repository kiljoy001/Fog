namespace NinePSharp.Fog.Client;

// What the console fog is run from gives it, as Program has it from System.Console and the
// machine: its streams and terminal, its keys and Ctrl-C, stty, and where it is.
public interface IConsole
{
    bool IsInputRedirected { get; }

    bool IsWindows { get; }

    // Whether /dev/tty is there to open.
    bool HasTty { get; }

    TextReader In { get; }

    string CurrentDirectory { get; }

    string UserName { get; }

    string? Environment(string name);

    Stream OpenStandardInput();

    Stream OpenStandardOutput();

    Stream OpenStandardError();

    // /dev/tty, to read the terminal when standard input is not it.
    TextReader OpenTty();

    ConsoleKeyInfo ReadKey();

    void WriteError(string text);

    void WriteErrorLine();

    // stty with its argument on /dev/tty.
    void Stty(string mode);

    // Windows' drives that are ready, by their names (C:) and roots.
    IReadOnlyDictionary<string, string> Drives();

    // Calls the handler at each Ctrl-C, which then does not end fog.
    void OnCancelKeyPress(Action handler);
}
