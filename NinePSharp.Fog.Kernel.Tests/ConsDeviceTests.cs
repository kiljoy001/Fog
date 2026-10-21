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

        Assert.Equal(["bintime 664 glenda", "cons 660 glenda", "null 666 glenda", "pid 444 glenda", "ppid 444 glenda", "sysname 664 glenda", "user 666 glenda", "zero 444 glenda"], stats);
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
    public async Task ConsWritesToTheHostsConsoleAndCannotBeReadOrOpenedByAnyoneButTheHostOwner()
    {
        var console = new CapturingConsole();
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda", console: console).BootAsync();
        await WriteAsync(init, "/dev/cons", "hello\n");
        await WriteAsync(init, "#c/cons", "again");
        Assert.Equal("hello\nagain", console.Text);

        int fd = await init.OpenAsync("/dev/cons", NinePSharp.Constants.NinePConstants.OREAD);
        Assert.Equal("the front fell off", (await Assert.ThrowsAsync<SyscallException>(() => init.ReadAsync(fd, 10).AsTask())).Message);

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

    private static async Task<string> ReadAsync(Process process, string path)
    {
        int fd = await process.OpenAsync(path, NinePSharp.Constants.NinePConstants.OREAD);
        string text = System.Text.Encoding.UTF8.GetString((await process.ReadAsync(fd, 1024)).Span);
        await process.CloseAsync(fd);
        return text;
    }

    private static async Task WriteAsync(Process process, string path, string text)
    {
        int fd = await process.OpenAsync(path, NinePSharp.Constants.NinePConstants.OWRITE);
        await process.WriteAsync(fd, System.Text.Encoding.UTF8.GetBytes(text));
        await process.CloseAsync(fd);
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
