using System.Text;
using NinePSharp.Fog.Client;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// fog's Local on a console: Ctrl-C as an interrupt, the terminal it reads, the password read key
// by key or from /dev/tty with stty, and the console's streams, directory, user and drives.
public sealed class ConsoleLocalTests
{
    [Fact]
    public async Task CtrlCIsAnInterrupt()
    {
        var console = new FakeConsole();
        Local local = ConsoleLocal.Create(console);
        console.Cancel!();
        console.Cancel();
        Assert.Equal("interrupt", await local.Interrupts.ReadAsync());
        Assert.Equal("interrupt", await local.Interrupts.ReadAsync());
    }

    // /dev/tty is read only when standard input is redirected, on a machine that is not Windows
    // and has one.
    [Theory]
    [InlineData(false, false, true, "in")]
    [InlineData(true, false, true, "tty")]
    [InlineData(true, true, true, "in")]
    [InlineData(true, false, false, "in")]
    public async Task TheConsoleIsReadFromTheTerminal(bool redirected, bool windows, bool tty, string from)
    {
        var console = new FakeConsole { IsInputRedirected = redirected, IsWindows = windows, HasTty = tty, In = new StringReader("in\n"), Tty = new StringReader("tty\n") };
        Local local = ConsoleLocal.Create(console);
        Assert.Equal(from + "\n", await local.ReadConsole(CancellationToken.None));
        Assert.Null(await local.ReadConsole(CancellationToken.None));
        Assert.Equal(from == "tty" ? 1 : 0, console.TtyOpens);
    }

    [Fact]
    public async Task ATerminalThatCannotBeOpenedIsEmpty()
    {
        var console = new FakeConsole { IsInputRedirected = true, HasTty = true, Tty = null };
        Assert.Null(await ConsoleLocal.Create(console).ReadConsole(CancellationToken.None));
    }

    // At a terminal the password is read key by key, a backspace taking back the key before it,
    // and the prompt is ended with a newline.
    [Fact]
    public async Task AtATerminalThePasswordIsTypedKeyByKey()
    {
        var console = new FakeConsole();
        console.Keys.Enqueue(Key(ConsoleKey.Backspace, '\b'));
        foreach (char c in "abx")
        {
            console.Keys.Enqueue(Key(ConsoleKey.A, c));
        }

        console.Keys.Enqueue(Key(ConsoleKey.Backspace, '\b'));
        console.Keys.Enqueue(Key(ConsoleKey.C, 'c'));
        console.Keys.Enqueue(Key(ConsoleKey.Enter, '\r'));
        Assert.Equal("abc", await ConsoleLocal.Create(console).ReadPassword("password: ", CancellationToken.None));
        Assert.Equal("password: \n", console.Errors.ToString());
        Assert.Empty(console.Sttys);
    }

    // With standard input redirected, the password is a line of /dev/tty, read with its echo off;
    // stty is not run where there is no tty or on Windows.
    [Theory]
    [InlineData(false, true, "tty", "-echo echo")]
    [InlineData(true, true, "in", "")]
    [InlineData(false, false, "in", "")]
    public async Task OtherwiseThePasswordIsALineOfTheTerminalWithItsEchoOff(bool windows, bool tty, string password, string sttys)
    {
        var console = new FakeConsole { IsInputRedirected = true, IsWindows = windows, HasTty = tty, In = new StringReader("in\n"), Tty = new StringReader("tty\n") };
        Assert.Equal(password, await ConsoleLocal.Create(console).ReadPassword("p: ", CancellationToken.None));
        Assert.Equal(sttys, string.Join(' ', console.Sttys));
        Assert.Equal("p: \n", console.Errors.ToString());
    }

    [Fact]
    public async Task EchoIsTurnedBackOnWhenTheReadFails()
    {
        var console = new FakeConsole { IsInputRedirected = true, HasTty = true, Tty = new ThrowingReader() };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ConsoleLocal.Create(console).ReadPassword("p: ", CancellationToken.None));
        Assert.Equal(["-echo", "echo"], console.Sttys);
        Assert.Equal("p: \n", console.Errors.ToString());
    }

    // While the terminal is raw a line of it is read as the password is, without a prompt, and once
    // it is cooked again a line is read as before.
    [Fact]
    public async Task AtATerminalARawLineIsTypedKeyByKey()
    {
        var console = new FakeConsole { In = new StringReader("cooked\n") };
        foreach (char c in "abx")
        {
            console.Keys.Enqueue(Key(ConsoleKey.A, c));
        }

        console.Keys.Enqueue(Key(ConsoleKey.Backspace, '\b'));
        console.Keys.Enqueue(Key(ConsoleKey.Enter, '\r'));
        Local local = ConsoleLocal.Create(console);
        local.Raw(true);
        Assert.Equal("ab\n", await local.ReadConsole(CancellationToken.None));
        local.Raw(false);
        Assert.Equal("cooked\n", await local.ReadConsole(CancellationToken.None));
        Assert.Equal(string.Empty, console.Errors.ToString());
        Assert.Empty(console.Keys);
    }

    [Fact]
    public async Task OtherwiseARawLineIsALineOfTheTerminalWithItsEchoOff()
    {
        var console = new FakeConsole { IsInputRedirected = true, HasTty = true, Tty = new StringReader("raw\ncooked\n") };
        Local local = ConsoleLocal.Create(console);
        local.Raw(true);
        Assert.Equal("raw\n", await local.ReadConsole(CancellationToken.None));
        Assert.Equal(["-echo", "echo"], console.Sttys);
        local.Raw(false);
        Assert.Equal("cooked\n", await local.ReadConsole(CancellationToken.None));
        local.Raw(true);
        Assert.Null(await local.ReadConsole(CancellationToken.None));
        Assert.Equal(["-echo", "echo", "-echo", "echo"], console.Sttys);
        Assert.Equal(string.Empty, console.Errors.ToString());
    }

    [Fact]
    public async Task TheConsolesStreamsDirectoryEnvironmentAndUserAreFogs()
    {
        var console = new FakeConsole();
        Local local = ConsoleLocal.Create(console);
        Assert.Same(console.StandardInput, local.Input);
        Assert.Same(console.StandardOutput, local.Output);
        Assert.Same(console.StandardError, local.Error);
        Assert.Equal("/home/glenda", local.Directory);
        Assert.Equal("tcp!fog!17019", local.Environment("cpu"));
        Assert.Null(local.Environment("auth"));
        Assert.Equal("glenda", local.LoginName);

        await local.WriteConsole("to the terminal"u8.ToArray(), CancellationToken.None);
        Assert.Equal("to the terminal", Encoding.UTF8.GetString(console.Written.ToArray()));
        Assert.True(console.WrittenClosed);
    }

    [Fact]
    public void OnlyWindowsHasDrives()
    {
        Assert.Empty(ConsoleLocal.Create(new FakeConsole()).Drives);
        Assert.Equal(new Dictionary<string, string> { ["C:"] = "C:\\" }, ConsoleLocal.Create(new FakeConsole { IsWindows = true }).Drives);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char c) => new(c, key, false, false, false);

    private sealed class ThrowingReader : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("read failed");
    }

    private sealed class FakeConsole : IConsole
    {
        private readonly ClosedStream output = new();
        private int opened;

        public bool IsInputRedirected { get; init; }

        public bool IsWindows { get; init; }

        public bool HasTty { get; init; }

        public TextReader In { get; init; } = new StringReader(string.Empty);

        // What /dev/tty gives, or null when it cannot be opened.
        public TextReader? Tty { get; init; } = new StringReader(string.Empty);

        public int TtyOpens { get; private set; }

        public string CurrentDirectory => "/home/glenda";

        public string UserName => "glenda";

        public Queue<ConsoleKeyInfo> Keys { get; } = new();

        public StringBuilder Errors { get; } = new();

        public List<string> Sttys { get; } = [];

        public Action? Cancel { get; private set; }

        public Stream StandardInput { get; } = new MemoryStream();

        public Stream StandardOutput { get; } = new MemoryStream();

        public Stream StandardError { get; } = new MemoryStream();

        public MemoryStream Written => output;

        public bool WrittenClosed => output.Closed;

        public string? Environment(string name) => name == "cpu" ? "tcp!fog!17019" : null;

        // The first is fog's standard output; each later one is a write to the terminal.
        public Stream OpenStandardOutput() => opened++ == 0 ? StandardOutput : output;

        public Stream OpenStandardInput() => StandardInput;

        public Stream OpenStandardError() => StandardError;

        public TextReader OpenTty()
        {
            TtyOpens++;
            return Tty ?? throw new IOException("no tty");
        }

        public ConsoleKeyInfo ReadKey() => Keys.Dequeue();

        public void WriteError(string text) => Errors.Append(text);

        public void WriteErrorLine() => Errors.Append('\n');

        public void Stty(string mode) => Sttys.Add(mode);

        public IReadOnlyDictionary<string, string> Drives() => new Dictionary<string, string> { ["C:"] = "C:\\" };

        public void OnCancelKeyPress(Action handler) => Cancel = handler;
    }

    private sealed class ClosedStream : MemoryStream
    {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
        }
    }
}
