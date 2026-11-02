using System.Diagnostics;
using System.Text;

namespace NinePSharp.Fog.Client;

// System.Console and the machine, as fog's IConsole.
internal sealed class SystemConsole : IConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool HasTty => File.Exists("/dev/tty");

    public TextReader In => Console.In;

    public string CurrentDirectory => System.Environment.CurrentDirectory;

    public string UserName => System.Environment.UserName;

    public string? Environment(string name) => System.Environment.GetEnvironmentVariable(name);

    public Stream OpenStandardInput() => Console.OpenStandardInput();

    public Stream OpenStandardOutput() => Console.OpenStandardOutput();

    public Stream OpenStandardError() => Console.OpenStandardError();

    public TextReader OpenTty() => new StreamReader(File.OpenRead("/dev/tty"), Encoding.UTF8);

    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public void WriteError(string text) => Console.Error.Write(text);

    public void WriteErrorLine() => Console.Error.WriteLine();

    // sh gives stty /dev/tty as GNU's and BSD's stty both take it.
    public void Stty(string mode)
    {
        try
        {
            using var stty = Process.Start(new ProcessStartInfo("sh", ["-c", $"stty {mode} </dev/tty"]) { UseShellExecute = false });
            stty?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public IReadOnlyDictionary<string, string> Drives()
        => DriveInfo.GetDrives().Where(d => d.IsReady).ToDictionary(d => d.Name.TrimEnd('\\'), d => d.RootDirectory.FullName);

    public void OnCancelKeyPress(Action handler)
        => Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            handler();
        };
}
