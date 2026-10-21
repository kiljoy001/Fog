using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// aux/listen over rounds of its minute, on a clock the tests move: the services it finds in its
// directories each round, what it logs, and how it gives up on one and takes it up again.
public sealed class ListenTests : IAsyncLifetime
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHostConsole console = new();
    private FogKernel kernel = null!;
    private Process init = null!;

    public async Task InitializeAsync()
    {
        var programs = new Dictionary<string, ProgramMain>(Commands.Commands.All) { ["rc"] = RcProgram.MainAsync };
        kernel = FogKernel.InMemory(programs, "glenda", clock, new FakeHostNetwork(), console);
        init = await kernel.BootAsync();
        await MkdirAsync("/root/rc/lib");
        await WriteAsync("/root/rc/lib/rcmain", RcProgram.Rcmain);
        await MkdirAsync("#R/sys");
        await MkdirAsync("#R/sys/log");
        await WriteAsync("/sys/log/listen", string.Empty, 0b110_110_110);
        await MkdirAsync("/tmp/svc");
    }

    public Task DisposeAsync()
    {
        foreach (Process process in kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task EachRoundTakesUpTheServicesAddedAndDropsThoseRemoved()
    {
        await ServiceAsync("tcp17130", "echo one");
        await MkdirAsync("/tmp/svc/tcp17139");
        await WriteAsync("/tmp/svc/tcp17138", string.Empty);
        await ServiceAsync("udp17137", "echo udp");
        await ServiceAsync("tcp17136.namespace", "bind /tmp /n");
        await ListenAsync("-q", "-t", "/tmp/svc", "-a", "127.0.0.1");
        Assert.Equal("one\n", await CallWhenAnsweredAsync(17130));

        await ServiceAsync("tcp17131", "echo two");
        await init.RemoveAsync("/tmp/svc/tcp17130");
        await RoundAsync();
        Assert.Equal("two\n", await CallWhenAnsweredAsync(17131));
        await WaitForAsync(async () => await CallAsync(17130) is null);
        foreach (int port in (int[])[17136, 17137, 17138, 17139])
        {
            Assert.Null(await CallAsync(port));
        }

        Assert.Equal("started on tcp", Logged().Single());

        // Each round's scan, sleep, alarm and log leaves no descriptor open behind it, once a round
        // in progress has finished with what it opened.
        const string Serving = "/\n  0 w  c    0 (0000000000000003 0 00)     0        0 /dev/null\n  2 w  c    0 (0000000000000003 0 00)     0        0 /dev/null\n";
        const string Announcer = "/\n  0 w  c    0 (0000000000000003 0 00)     0        0 /dev/null\n  1 rw I    0 (0000000000000006 0 00)     0       25 /net/tcp/clone\n  2 w  c    0 (0000000000000003 0 00)     0        0 /dev/null\n";
        await WaitForAsync(async () => await FdsAsync("listen [tcp 127.0.0.1 /tmp/svc]") == Serving && await FdsAsync("tcp!127.0.0.1!17131]") == Announcer);
    }

    [Fact]
    public async Task AServiceWhosePortIsInUseIsLoggedOnceAndTakenUpWhenItIsFree()
    {
        // The port is held once listen has started, so listen does not hold it too.
        await ServiceAsync("tcp17132", "echo free");
        await ListenAsync("-q", "-t", "/tmp/svc", "-a", "127.0.0.1");
        int held = await HoldAsync(17132);
        await WaitForAsync(() => Task.FromResult(Logged().Count == 2));
        await RoundAsync();
        await RoundAsync();
        Assert.Equal(["started on tcp", "giving up on tcp!127.0.0.1!17132: announce writing /net/tcp: address in use"], Logged());
        await init.CloseAsync(held);
        await RoundAsync();
        Assert.Equal("free\n", await CallWhenAnsweredAsync(17132));
    }

    [Fact]
    public async Task ACallIsLoggedUnlessQuietAndAServiceThatCannotRunOrHasABadNamespaceIsLogged()
    {
        await ServiceAsync("tcp17133", "echo logged");
        await WriteAsync("/tmp/svc/tcp17134", "not a program");
        await ServiceAsync("tcp17135", "echo never");
        await WriteAsync("/tmp/svc/tcp17135.namespace", ". /lib/namespace\n", 0);
        await ListenAsync("-d", "/tmp/svc", "-a", "127.0.0.1", "-O", "bogus", "-o", "bogus2", "-n", "/lib/namespace", "-p", "3");
        Assert.Equal("logged\n", await CallWhenAnsweredAsync(17133));

        // The error is on the console too, which the call's data is bound over.
        Assert.Equal(" Oct 19 12:00:00 tcp: /tmp/svc/tcp17134: exec header invalid\n", await CallWhenAnsweredAsync(17134));
        List<string> logged = Logged();
        Assert.Contains("started on tcp", logged);
        Assert.Contains(logged, l => Regex.IsMatch(l, @"^tcp call for tcp17133 on chan /net/tcp/\d+ \(127\.0\.0\.1\)$"));
        Assert.Contains(logged, l => Regex.IsMatch(l, @"^/net/tcp/\d+/ctl: can't write bogus: unknown control request$"));
        Assert.Contains("tcp: /tmp/svc/tcp17134: exec header invalid", logged);
        await WaitForAsync(() => Task.FromResult(Logged().Any(l => l.StartsWith("can't build namespace /tmp/svc/tcp17135.namespace: ", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task AListenThatFailsIsLoggedAndThePortAnnouncedAgain()
    {
        await ServiceAsync("tcp17140", "echo again");
        await ListenAsync("-t", "/tmp/svc", "-a", "127.0.0.1");
        Assert.Equal("again\n", await CallWhenAnsweredAsync(17140));
        string dir = Regex.Match(string.Join('\n', Logged()), @"on chan (/net/tcp/)\d+").Groups[1].Value;
        string announced = await AnnouncedDirAsync(17140);
        int ctl = await init.OpenAsync($"{announced}/ctl", NinePConstants.ORDWR);
        await init.WriteAsync(ctl, "hangup"u8.ToArray());
        await init.CloseAsync(ctl);
        await WaitForAsync(() => Task.FromResult(Logged().Any(l => l.StartsWith("listen: ", StringComparison.Ordinal))));
        Assert.Equal("again\n", await CallWhenAnsweredAsync(17140));
        Assert.Equal("/net/tcp/", dir);
    }

    [Fact]
    public async Task ADirectoryTooLongForAServicesPathIsAnError()
    {
        await ListenAsync("-d", "/" + new string('d', 959));
        await ListenAsync("-t", "/" + new string('t', 959));
        Assert.Equal(["tcp: service directory too long: ", "tcp: trusted service directory too long: "], Logged());
    }

    [Fact]
    public async Task OptionsNeedTheirValuesAndAreAtMostSixteenOfAKind()
    {
        foreach (string flag in (string[])["-a", "-d", "-t", "-n", "-p", "-o", "-O", "-z"])
        {
            Assert.Equal("usage", await RunAsync(flag));
        }

        Assert.Equal("usage", await RunAsync("tcp", "udp"));
        Assert.Equal("too many -o options", await RunAsync([.. Enumerable.Range(0, 17).SelectMany(i => (string[])["-o", "x"])]));
        Assert.Equal("too many -O options", await RunAsync([.. Enumerable.Range(0, 17).SelectMany(i => (string[])["-O", "x"])]));
    }

    [Fact]
    public async Task AProtocolNamedWithItsDirectoryIsAnnouncedThereAndItsServicesAreNamedForItsLastElement()
    {
        await ServiceAsync("tcp17141", "echo alt");
        await init.BindAsync("#I", "/net.alt", NinePSharp.Namespaces.MountFlags.Replace);
        await ListenAsync("-q", "-t", "/tmp/svc", "-a", "127.0.0.1", "/net.alt/tcp");
        Assert.Equal("alt\n", await CallWhenAnsweredAsync(17141));
        Assert.Equal("started on /net.alt/tcp", Logged().Single());
    }

    [Fact]
    public async Task WithoutDirectoriesTheServicesAreBinServices()
    {
        await ListenAsync("-q");
        Assert.Contains(kernel.Processes.Values, p => p.Args.Contains("tcp * /bin/service", StringComparison.Ordinal));
    }

    // The condition, waited for while the clock moves on a little at a time, as listen staggers
    // each announcement by a sleep.
    private async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }

            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(50);
        }

        Assert.Fail($"the condition never held; listen logged: {string.Join(" | ", Logged())}; processes: {string.Join(" | ", kernel.Processes.Values.Select(p => $"{p.Pid}:{p.Args}"))}");
    }

    private async Task ServiceAsync(string name, string body) => await WriteAsync($"/tmp/svc/{name}", $"#!/bin/rc\n{body}\n");

    // aux/listen, which starts its services' processes and exits.
    private async Task ListenAsync(params string[] arguments)
    {
        Assert.Equal(string.Empty, await RunAsync(arguments));
    }

    // aux/listen's exit status, without the name and pid it is reported with.
    private async Task<string> RunAsync(params string[] arguments)
    {
        init.Fork(RforkFlags.Proc, async child =>
        {
            await child.DupAsync(await child.OpenAsync("/dev/null", NinePConstants.OWRITE), 2);
            await child.ExecAsync("/bin/aux/listen", ["aux/listen", .. arguments]);
        });
        string message = (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message;
        return message.Length == 0 ? message : message[(message.IndexOf(": ", StringComparison.Ordinal) + 2)..];
    }

    // A minute of the clock, in steps, so each sleep and alarm that falls due is taken.
    private async Task RoundAsync()
    {
        for (int i = 0; i < 62; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
    }

    private async Task<string> CallWhenAnsweredAsync(int port)
    {
        string? answer = null;
        await WaitForAsync(async () => (answer = await CallAsync(port)) is not null);
        return answer!;
    }

    // What a call to the port gives before it hangs up, or null when it is refused.
    private async Task<string?> CallAsync(int port)
    {
        int ctl = await init.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
        try
        {
            string n = Encoding.UTF8.GetString((await init.ReadAsync(ctl, 32)).Span);
            try
            {
                await init.WriteAsync(ctl, Encoding.UTF8.GetBytes($"connect 127.0.0.1!{port}"));
            }
            catch (SyscallException)
            {
                return null;
            }

            int data = await init.OpenAsync($"/net/tcp/{n}/data", NinePConstants.ORDWR);
            var all = new List<byte>();
            try
            {
                for (ReadOnlyMemory<byte> b; !(b = await init.ReadAsync(data, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(2))).IsEmpty;)
                {
                    all.AddRange(b.Span);
                }
            }
            catch (SyscallException)
            {
                // The service hung up.
            }
            catch (TimeoutException)
            {
                Assert.Fail($"the call to {port} was never answered; listen logged: {string.Join(" | ", Logged())}; processes: {string.Join(" | ", kernel.Processes.Values.Select(p => $"{p.Pid}:{p.Args}"))}; conversations: {ConversationsAsync().GetAwaiter().GetResult()}");
            }

            await init.CloseAsync(data);
            return Encoding.UTF8.GetString([.. all]);
        }
        finally
        {
            await init.CloseAsync(ctl);
        }
    }

    // A conversation announced on the port, held until its ctl is closed.
    private async Task<int> HoldAsync(int port)
    {
        int ctl = await init.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
        await init.ReadAsync(ctl, 32);
        await init.WriteAsync(ctl, Encoding.UTF8.GetBytes($"announce 127.0.0.1!{port}"));
        return ctl;
    }

    // The directory of the conversation announced on the port, as its local reads.
    private async Task<string> AnnouncedDirAsync(int port)
    {
        int dir = await init.OpenAsync("/net/tcp", NinePConstants.OREAD);
        IReadOnlyList<NinePSharp.Messages.Stat> entries = await init.DirReadAsync(dir);
        await init.CloseAsync(dir);
        foreach (string n in entries.Select(e => e.Name).Where(n => n.All(char.IsAsciiDigit)))
        {
            try
            {
                if ((await ReadAsync($"/net/tcp/{n}/local")).Trim().EndsWith($"!{port}", StringComparison.Ordinal) && (await ReadAsync($"/net/tcp/{n}/status")).StartsWith("Listen", StringComparison.Ordinal))
                {
                    return $"/net/tcp/{n}";
                }
            }
            catch (SyscallException)
            {
                // Gone since.
            }
        }

        throw new InvalidOperationException($"nothing is announced on {port}");
    }

    // The fd file of the process whose args end so, or nothing while there is not exactly one.
    private async Task<string> FdsAsync(string args)
    {
        Process[] found = [.. kernel.Processes.Values.Where(p => p.Args.EndsWith(args, StringComparison.Ordinal))];
        try
        {
            return found.Length == 1 ? await ReadAsync($"/proc/{found[0].Pid}/fd") : string.Empty;
        }
        catch (SyscallException)
        {
            return string.Empty;
        }
    }

    private async Task<string> ConversationsAsync()
    {
        int dir = await init.OpenAsync("/net/tcp", NinePConstants.OREAD);
        IReadOnlyList<NinePSharp.Messages.Stat> entries = await init.DirReadAsync(dir);
        await init.CloseAsync(dir);
        var all = new List<string>();
        foreach (string n in entries.Select(e => e.Name).Where(n => n.All(char.IsAsciiDigit)))
        {
            all.Add($"{n}: {(await ReadAsync($"/net/tcp/{n}/status")).Trim()} {(await ReadAsync($"/net/tcp/{n}/local")).Trim()} {(await ReadAsync($"/net/tcp/{n}/remote")).Trim()}");
        }

        return string.Join(" | ", all);
    }

    // The log's messages, without the system's name and the time.
    private List<string> Logged()
        => [.. Encoding.UTF8.GetString(ReadAllAsync("/sys/log/listen").GetAwaiter().GetResult()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => Regex.Replace(l, @"^\S* ?\w{3} [ \d]\d \d\d:\d\d:\d\d ", string.Empty))];

    private async Task<string> ReadAsync(string path) => Encoding.UTF8.GetString(await ReadAllAsync(path));

    private async Task<byte[]> ReadAllAsync(string path)
    {
        int f = await init.OpenAsync(path, NinePConstants.OREAD);
        var all = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await init.ReadAsync(f, 8192)).IsEmpty;)
        {
            all.AddRange(b.Span);
        }

        await init.CloseAsync(f);
        return [.. all];
    }

    private async Task MkdirAsync(string path)
        => await init.CloseAsync(await init.CreateAsync(path, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));

    private async Task WriteAsync(string path, string text, uint mode = 0b111_111_101)
    {
        int f = await init.CreateAsync(path, NinePConstants.OWRITE, mode);
        await init.WriteAsync(f, Encoding.UTF8.GetBytes(text));
        await init.CloseAsync(f);
    }
}
