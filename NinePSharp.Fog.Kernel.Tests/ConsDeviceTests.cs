using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

public sealed class ConsDeviceTests
{
    private static readonly NinePSharp.Namespaces.ResourceOperationContext Context = new(new NinePSharp.Namespaces.ResourceOperationId("test", 1), 1, "glenda");

    [Fact]
    public async Task TheDirectoryIsHashCAndNothingCanBeCreatedOrRemoved()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        var stat = await init.Cons.StatAsync(ConsDevice.Root, default);
        Assert.Equal(("#c", 0x80000000u | 0b101_101_101), (stat.Name, stat.Mode));
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => init.Cons.CreateAsync(ConsDevice.Root, "x", false, default)).Message);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => init.Cons.CreateAndOpenAsync(ConsDevice.Root, "x", 0, 0, Context, default)).Message);
        Assert.Equal("permission denied", Assert.Throws<IOException>(() => init.Cons.RemoveAsync(ConsDevice.Root, null, Context, default)).Message);
    }

    [Fact]
    public async Task AProcessThatIsNotTheHostOwnerHasTheOthersPermissions()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        int user = await init.OpenAsync("/dev/user", NinePSharp.Constants.NinePConstants.OWRITE);
        await init.WriteAsync(user, "none"u8.ToArray());

        Assert.Equal("permission denied: '/dev/bintime'", (await Assert.ThrowsAsync<SyscallException>(() => init.OpenAsync("/dev/bintime", NinePSharp.Constants.NinePConstants.OWRITE).AsTask())).Message);
    }

    [Fact]
    public async Task EachFileStatsWithItsNameAndPermissions()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        var stats = new List<string>();
        foreach (var entry in await init.Cons.ReadDirectoryAsync(ConsDevice.Root, default))
        {
            var stat = await init.Cons.StatAsync(entry.Handle, default);
            stats.Add($"{stat.Name} {Convert.ToString(stat.Mode, 8)} {stat.User}");
        }

        Assert.Equal(["bintime 664 glenda", "cons 660 glenda", "consctl 220 glenda", "hostowner 664 glenda", "null 666 glenda", "nvram 664 glenda", "pid 444 glenda", "ppid 444 glenda", "sysname 664 glenda", "user 666 glenda", "zero 444 glenda"], stats);
    }

    [Fact]
    public async Task SysnameIsTheKernelsAsTheHostOwnerLastWroteItAtItsStartWithoutALastNewline()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        Assert.Equal(string.Empty, await ReadAsync(init, "/dev/sysname"));
        await WriteAsync(init, "/dev/sysname", "fog\n");
        Assert.Equal("fog", await ReadAsync(init, "/dev/sysname"));
        await WriteAsync(init, "/dev/sysname", "fogtest\0x");
        Assert.Equal("fogtest", await ReadAsync(init, "/dev/sysname"));
        await WriteAsync(init, "/dev/sysname", "\0x");
        Assert.Equal(string.Empty, await ReadAsync(init, "/dev/sysname"));
        await WriteAsync(init, "/dev/sysname", new string('x', 255));
        Assert.Equal(new string('x', 255), await ReadAsync(init, "/dev/sysname"));

        int fd = await init.OpenAsync("/dev/sysname", NinePSharp.Constants.NinePConstants.OWRITE);
        Assert.Equal("bad arg in system call", (await Assert.ThrowsAsync<SyscallException>(() => init.WriteAsync(fd, new byte[256]).AsTask())).Message);
        Assert.Equal("bad arg in system call", (await Assert.ThrowsAsync<SyscallException>(() => init.WriteAsync(fd, Array.Empty<byte>()).AsTask())).Message);
        Assert.Equal("bad arg in system call", (await Assert.ThrowsAsync<SyscallException>(() => init.PWriteAsync(fd, "a"u8.ToArray(), 1).AsTask())).Message);

        // Another process reads the same name, and one that is not the host owner may not write it.
        Process other = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        Assert.Equal(string.Empty, await ReadAsync(other, "/dev/sysname"));
        long child = init.Fork(RforkFlags.Proc, async c => Assert.Equal(new string('x', 255), await ReadAsync(c, "/dev/sysname")));
        Assert.Equal(child, (await init.WaitAsync()).Pid);
        int user = await init.OpenAsync("/dev/user", NinePSharp.Constants.NinePConstants.OWRITE);
        await init.WriteAsync(user, "none"u8.ToArray());
        Assert.Equal("permission denied: '/dev/sysname'", (await Assert.ThrowsAsync<SyscallException>(() => init.OpenAsync("/dev/sysname", NinePSharp.Constants.NinePConstants.OWRITE).AsTask())).Message);
        Assert.Equal(new string('x', 255), await ReadAsync(init, "/dev/sysname"));
    }

    [Fact]
    public async Task ConsWritesToTheHostsConsoleReadsWhatIsTypedThereAndCannotBeOpenedByAnyoneButTheHostOwner()
    {
        var console = new Support.TypingConsole();
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda", console: console).BootAsync();
        await WriteAsync(init, "/dev/cons", "hello\n");
        await WriteAsync(init, "#c/cons", "again");
        Assert.Equal("hello\nagain", console.Written);

        int fd = await init.OpenAsync("/dev/cons", NinePSharp.Constants.NinePConstants.OREAD);
        Assert.Equal(0, (await init.ReadAsync(fd, 0).AsTask().WaitAsync(TimeSpan.FromSeconds(1))).Length);
        console.Type("typed\n");
        Assert.Equal("typed\n", System.Text.Encoding.UTF8.GetString((await init.ReadAsync(fd, 10)).Span));

        int user = await init.OpenAsync("/dev/user", NinePSharp.Constants.NinePConstants.OWRITE);
        await init.WriteAsync(user, "none"u8.ToArray());
        Assert.Equal("permission denied: '/dev/cons'", (await Assert.ThrowsAsync<SyscallException>(() => init.OpenAsync("/dev/cons", NinePSharp.Constants.NinePConstants.OWRITE).AsTask())).Message);
        Assert.Equal("permission denied: '/dev/cons'", (await Assert.ThrowsAsync<SyscallException>(() => init.OpenAsync("/dev/cons", NinePSharp.Constants.NinePConstants.OREAD).AsTask())).Message);
    }

    [Fact]
    public async Task TheHostsConsoleIsItsStandardError()
    {
        TextWriter error = Console.Error;
        var captured = new FlushedWriter();
        Console.SetError(captured);
        try
        {
            HostConsole.System.Write("to the host\n"u8);
        }
        finally
        {
            Console.SetError(error);
        }

        Assert.Equal("to the host\n", captured.Flushed);
        Assert.Same(HostConsole.System, (await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync()).Kernel.Console);
    }

    [Fact]
    public async Task ACookedReadInterruptedKeepsWhatWasTypedOfItsLineForARawReadAndRawoffDropsIt()
    {
        var console = new Support.TypingConsole();
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda", console: console).BootAsync();
        int cons = await init.OpenAsync("/dev/cons", NinePSharp.Constants.NinePConstants.OREAD);
        int ctl = await init.OpenAsync("/dev/consctl", NinePSharp.Constants.NinePConstants.OWRITE);

        // The line so far goes to kbdq on rawon, and a raw read gives it without asking the host.
        await InterruptedAsync(init, console, cons, "abc");
        await init.WriteAsync(ctl, "rawon"u8.ToArray());
        Assert.Equal("ab", await ReadAsync(init, cons, 2));
        Assert.Equal("c", await ReadAsync(init, cons, 10));
        console.Type("de");
        Assert.Equal("de", await ReadAsync(init, cons, 10));

        // rawoff drops what was typed of a line.
        await init.WriteAsync(ctl, "rawoff"u8.ToArray());
        await InterruptedAsync(init, console, cons, "xy");
        await init.WriteAsync(ctl, "rawoff"u8.ToArray());
        console.Type("z\n");
        Assert.Equal("z\n", await ReadAsync(init, cons, 10));

        // An interrupted line is kept for the next cooked read, and rawon with no line keeps nothing.
        await InterruptedAsync(init, console, cons, "pq");
        console.Type("r\n");
        Assert.Equal("pqr\n", await ReadAsync(init, cons, 10));
        await init.WriteAsync(ctl, "rawon"u8.ToArray());
        console.Type("s");
        Assert.Equal("s", await ReadAsync(init, cons, 10));
    }

    [Fact]
    public async Task ConsctlLooksAtWordsInItsFirst255BytesUpToANul()
    {
        var console = new Support.TypingConsole();
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda", console: console).BootAsync();
        int ctl = await init.OpenAsync("/dev/consctl", NinePSharp.Constants.NinePConstants.OWRITE);
        foreach (string ignored in new[] { new string('x', 250) + " rawon", "xrawon", "x\0 rawon", "\0 rawon", "raw on" })
        {
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(ignored), await init.WriteAsync(ctl, System.Text.Encoding.UTF8.GetBytes(ignored)));
            Assert.False(console.Raw);
        }

        await init.WriteAsync(ctl, System.Text.Encoding.UTF8.GetBytes(new string('x', 248) + " rawon"));
        Assert.True(console.Raw);
        await init.WriteAsync(ctl, "x rawoff y"u8.ToArray());
        Assert.False(console.Raw);
        await init.WriteAsync(ctl, "x  rawon"u8.ToArray());
        Assert.True(console.Raw);

        // Closing another of #c's files, or #c itself, leaves consctl's count as it was.
        await init.CloseAsync(await init.OpenAsync("/dev/null", NinePSharp.Constants.NinePConstants.OREAD));
        await init.CloseAsync(await init.OpenAsync("#c", NinePSharp.Constants.NinePConstants.OREAD));
        Assert.True(console.Raw);
        await init.WriteAsync(ctl, "rawon rawoff"u8.ToArray());
        Assert.False(console.Raw);
    }

    [Fact]
    public async Task ConsctlIsClosedOnlyOnceAReadWaitingForALineHasOne()
    {
        var console = new Support.TypingConsole();
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda", console: console).BootAsync();
        int cons = await init.OpenAsync("/dev/cons", NinePSharp.Constants.NinePConstants.OREAD);
        int ctl = await init.OpenAsync("/dev/consctl", NinePSharp.Constants.NinePConstants.OWRITE);
        Task<ReadOnlyMemory<byte>> read = init.ReadAsync(cons, 10).AsTask();
        console.Type("a");
        while (console.Unread != 0)
        {
            await Task.Delay(1);
        }

        Task closed = init.CloseAsync(ctl).AsTask();
        await Task.Delay(100);
        Assert.False(closed.IsCompleted);
        console.Type("\n");
        Assert.Equal("a\n", System.Text.Encoding.UTF8.GetString((await read.WaitAsync(TimeSpan.FromSeconds(3))).Span));
        await closed.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ANoteInterruptsAReadOfCons()
    {
        var console = new Support.TypingConsole();
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda", console: console).BootAsync();
        int cons = await init.OpenAsync("/dev/cons", NinePSharp.Constants.NinePConstants.OREAD);
        init.Notify(note => note == "interrupt");
        Task<ReadOnlyMemory<byte>> read = init.ReadAsync(cons, 10).AsTask();
        while (!init.PostNote("interrupt"))
        {
        }

        Assert.Equal("interrupted", (await Assert.ThrowsAsync<SyscallException>(() => read.WaitAsync(TimeSpan.FromSeconds(3)))).Message);
    }

    [Fact]
    public async Task ANewHostOwnerOwnsTheKernelsFilesAndTakesOnlyWhatWasTheOldOnes()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        var none = new TaskCompletionSource<Process>();
        var release = new TaskCompletionSource();
        init.Fork(RforkFlags.Proc, async c =>
        {
            int user = await c.OpenAsync("/dev/user", NinePSharp.Constants.NinePConstants.OWRITE);
            await c.WriteAsync(user, "none"u8.ToArray());
            int fd = await c.CreateAsync("/srv/nones", NinePSharp.Constants.NinePConstants.OWRITE, 0b110_110_110);
            await c.WriteAsync(fd, "0"u8.ToArray());
            none.SetResult(c);
            await release.Task;
        });
        Process other = await none.Task.WaitAsync(TimeSpan.FromSeconds(3));
        int board = await init.OpenAsync("/srv/clone", NinePSharp.Constants.NinePConstants.OREAD);
        string name = System.Text.Encoding.UTF8.GetString((await init.ReadAsync(board, 32)).Span);
        (int pipe, _) = await init.PipeAsync();

        var made = new TaskCompletionSource<string>();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async c =>
        {
            int user = await c.OpenAsync("/dev/user", NinePSharp.Constants.NinePConstants.OWRITE);
            await c.WriteAsync(user, "none"u8.ToArray());
            int clone = await c.OpenAsync("/srv/clone", NinePSharp.Constants.NinePConstants.OREAD);
            made.SetResult(System.Text.Encoding.UTF8.GetString((await c.ReadAsync(clone, 32)).Span));
            await release.Task;
        });
        string nones = await made.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await WriteAsync(init, "/dev/hostowner", "bootes");
        Assert.Equal(("bootes", "none"), (init.User, other.User));
        Assert.Equal("none", (await init.StatAsync($"/srv/{nones}")).Uid);
        Assert.Equal("none", (await init.StatAsync("/srv/nones")).Uid);
        Assert.Equal("bootes", (await init.StatAsync("/srv")).Uid);
        Assert.Equal("bootes", (await init.StatAsync($"/srv/{name}")).Uid);
        Assert.Equal("bootes", (await init.StatAsync("#/")).Uid);
        Assert.Equal("bootes", (await init.FStatAsync(pipe)).Uid);
        Assert.Equal("bootes", (await init.StatAsync("/fd")).Uid);
        Assert.Equal("bootes", (await init.StatAsync("/fd/0")).Uid);
        Assert.Equal("bootes", (await init.StatAsync("/dev")).Uid);
        release.SetResult();
    }

    [Fact]
    public async Task HostownerIsWrittenOnlyByTheHostOwnerEvenThroughADescriptorOpenedAsItAndANameOfNothingIsRefused()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        int fd = await init.OpenAsync("/dev/hostowner", NinePSharp.Constants.NinePConstants.OWRITE);
        Assert.Equal("bad arg in system call", (await Assert.ThrowsAsync<SyscallException>(() => init.WriteAsync(fd, "\0x"u8.ToArray()).AsTask())).Message);
        Assert.Equal("name too long", (await Assert.ThrowsAsync<SyscallException>(() => init.WriteAsync(fd, new byte[28]).AsTask())).Message);
        Assert.Equal("glenda", await ReadAsync(init, "/dev/hostowner"));
        await WriteAsync(init, "/dev/user", "none");
        Assert.Equal("permission denied", (await Assert.ThrowsAsync<SyscallException>(() => init.WriteAsync(fd, "x"u8.ToArray()).AsTask())).Message);
    }

    [Fact]
    public async Task NvramReadsAndWritesNothingPastItsEndAndAHostWithoutOneHasNone()
    {
        byte[] contents = "abcd"u8.ToArray();
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda", nvram: new HostNvram(contents)).BootAsync();
        int fd = await init.OpenAsync("/dev/nvram", NinePSharp.Constants.NinePConstants.ORDWR);
        Assert.Equal("cd", System.Text.Encoding.UTF8.GetString((await init.PReadAsync(fd, 10, 2)).Span));
        Assert.Equal("b", System.Text.Encoding.UTF8.GetString((await init.PReadAsync(fd, 1, 1)).Span));
        Assert.Equal(0, (await init.PReadAsync(fd, 10, 9)).Length);
        Assert.Equal(0, await init.PWriteAsync(fd, "xy"u8.ToArray(), 9));
        Assert.Equal(1, await init.PWriteAsync(fd, "xy"u8.ToArray(), 3));
        Assert.Equal("abcx", System.Text.Encoding.UTF8.GetString(contents));

        Process bare = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync();
        Assert.Same(HostNvram.None, bare.Kernel.Nvram);
        Assert.Equal(0, (await bare.ReadAsync(await bare.OpenAsync("/dev/nvram", NinePSharp.Constants.NinePConstants.OREAD), 10)).Length);
    }

    [Fact]
    public async Task TheHostsConsoleReadsItsInputAsItComesOrAKeyAtATimeFromARawTerminal()
    {
        var keys = new Queue<char>("k\r");
        var console = new InjectedConsole(new OneCharReader("hé😀\n"), () => true, () => new ConsoleKeyInfo(keys.Dequeue(), ConsoleKey.K, false, false, false));
        var b = new byte[8];

        // Cooked, the input as it comes, a rune's bytes over several reads and a surrogate pair whole.
        Assert.Equal(1, await console.ReadAsync(b.AsMemory(0, 1), default));
        Assert.Equal((byte)'h', b[0]);
        Assert.Equal(1, await console.ReadAsync(b.AsMemory(0, 1), default));
        Assert.Equal(1, await console.ReadAsync(b.AsMemory(1, 1), default));
        Assert.Equal("é", System.Text.Encoding.UTF8.GetString(b, 0, 2));
        Assert.Equal(4, await console.ReadAsync(b, default));
        Assert.Equal("😀", System.Text.Encoding.UTF8.GetString(b, 0, 4));

        // Raw at a terminal, a key at a time; raw input that is not a terminal is read as it comes.
        console.SetTerm(true);
        Assert.Equal(1, await console.ReadAsync(b, default));
        Assert.Equal((byte)'k', b[0]);
        Assert.Equal(1, await console.ReadAsync(b, default));
        Assert.Equal((byte)'\r', b[0]);
        console.SetTerm(false);
        Assert.Equal(1, await console.ReadAsync(b, default));
        Assert.Equal((byte)'\n', b[0]);
        Assert.Equal(0, await console.ReadAsync(b, default));

        var piped = new InjectedConsole(new StringReader("ab"), () => false, () => throw new InvalidOperationException());
        piped.SetTerm(true);
        Assert.Equal(2, await piped.ReadAsync(b.AsMemory(0, 1), default) + await piped.ReadAsync(b.AsMemory(1, 1), default));
        Assert.Equal("ab", System.Text.Encoding.UTF8.GetString(b, 0, 2));
    }

    [Fact]
    public async Task TheHostsOwnConsoleReadsItsStandardInputRawOrNotWhenItIsNoTerminal()
    {
        TextReader input = Console.In;
        Assert.True(Console.IsInputRedirected);
        Console.SetIn(new StringReader("r"));
        try
        {
            var console = new HostConsole();
            console.SetTerm(true);
            var b = new byte[4];
            Assert.Equal(1, await console.ReadAsync(b, default));
            Assert.Equal((byte)'r', b[0]);
        }
        finally
        {
            Console.SetIn(input);
        }
    }

    private static async Task InterruptedAsync(Process process, Support.TypingConsole console, int fd, string typed)
    {
        console.Type(typed);
        using var cancel = new CancellationTokenSource();
        Task<ReadOnlyMemory<byte>> read = process.ReadAsync(fd, 10, cancel.Token).AsTask();
        while (console.Unread != 0)
        {
            await Task.Delay(1);
        }

        await Task.Delay(20);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    private static async Task<string> ReadAsync(Process process, int fd, int count)
        => System.Text.Encoding.UTF8.GetString((await process.ReadAsync(fd, count).AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Span);

    private static async Task<string> ReadAsync(Process process, string path)
    {
        int fd = await process.OpenAsync(path, NinePSharp.Constants.NinePConstants.OREAD);
        string text = System.Text.Encoding.UTF8.GetString((await process.ReadAsync(fd, 1024).AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Span);
        await process.CloseAsync(fd);
        return text;
    }

    private static async Task WriteAsync(Process process, string path, string text)
    {
        int fd = await process.OpenAsync(path, NinePSharp.Constants.NinePConstants.OWRITE);
        await process.WriteAsync(fd, System.Text.Encoding.UTF8.GetBytes(text));
        await process.CloseAsync(fd);
    }

    private sealed class InjectedConsole(TextReader input, Func<bool> terminal, Func<ConsoleKeyInfo> key) : HostConsole(() => input, terminal, key)
    {
    }

    // A reader that gives one character at a time, as a terminal may.
    private sealed class OneCharReader(string text) : TextReader
    {
        private int next;

        public override int Read(char[] buffer, int index, int count)
        {
            if (next == text.Length)
            {
                return 0;
            }

            buffer[index] = text[next++];
            return 1;
        }
    }

    private sealed class CapturingConsole : HostConsole
    {
        private readonly global::System.Text.StringBuilder text = new();

        public string Text => text.ToString();

        public override void Write(ReadOnlySpan<byte> data) => text.Append(global::System.Text.Encoding.UTF8.GetString(data));
    }

    // What had been written each time the writer was flushed.
    private sealed class FlushedWriter : StringWriter
    {
        public string Flushed { get; private set; } = string.Empty;

        public override void Flush() => Flushed = ToString();
    }
}
