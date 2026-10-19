using System.Text;
using System.Text.RegularExpressions;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests.Steps;

[Binding]
[Scope(Feature = "rc runs scripts as 9front rc does")]
public sealed class RcExecutionSteps
{
    private const string Script = "/tmp/s";
    private const int MaxOutput = 1 << 20;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(3);

    // Each read is bounded, and so is the whole run, so an rc that keeps printing fails the scenario too.
    private static readonly TimeSpan RunBound = TimeSpan.FromSeconds(10);
    private readonly ManualClock clock = new() { Now = DateTimeOffset.UtcNow };
    private Process? init;
    private string script = string.Empty;
    private string output = string.Empty;
    private string oracle = string.Empty;

    // The highest descriptor rc starts with in use.
    private int lastBusy = 2;

    // Whether 9front cannot be set up or typed the same way.
    private bool fogOnly;

    // rc's argv[0]; a login shell's starts with -.
    private string name = "/bin/rc";

    // A file server's end of a pipe whose other end rc starts with as descriptor 3.
    private FileServerHarness? server;
    private int served = -1;

    [Given(@"^a kernel whose /bin holds rc, echo and cat$")]
    public async Task GivenAKernel()
    {
        var programs = new Dictionary<string, ProgramMain>(Commands.Commands.All) { ["rc"] = RcProgram.MainAsync };
        init = await FogKernel.InMemory(programs, "glenda", clock).BootAsync();
        await init.CloseAsync(await init.CreateAsync("/root/rc/lib", NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));
        await WriteAsync("/root/rc/lib/rcmain", RcProgram.Rcmain);
    }

    [Given("rc starts with all but two of its 5000 descriptors in use")]
    public void GivenDescriptorsInUse() => (lastBusy, fogOnly) = (4997, true);

    [Given(@"^9front.*, so this runs on Fog alone$")]
    public void GivenFogAlone() => fogOnly = true;

    [Given("rc is started as a login shell")]
    public void GivenLogin() => (name, fogOnly) = ("-rc", true);

    [Given("rc starts with all but one of its 5000 descriptors in use")]
    public void GivenAllButOneInUse() => (lastBusy, fogOnly) = (4998, true);

    [Given(@"^""(.*)"" holds ""(.*)""$")]
    public Task GivenHolds(string path, string text) => WriteAsync(path, text);

    [Given(@"^""(.*)"" holds the line ""(.*)""$")]
    public Task GivenHoldsLine(string path, string text) => WriteAsync(path, text + "\n");

    [Given(@"^the executable ""(.*)"" holds ""(.*)"", which 9front would need chmod for$")]
    public Task GivenExecutable(string path, string text)
    {
        fogOnly = true;
        return WriteAsync(path, text + "\n");
    }

    // 9front's rc cannot be handed this file server, so these run on Fog alone.
    [Given(@"^a file server holding ""(.*)"" with ""(.*)"", on descriptor 3$")]
    public Task GivenFileServer(string file, string text) => ServeAsync(file, text, new FileServerHarness());

    [Given(@"^a file server holding ""(.*)"" with ""(.*)"", on descriptor 3, giving at most (\d+) bytes? a read$")]
    public Task GivenLimitedFileServer(string file, string text, int limit) => ServeAsync(file, text, new FileServerHarness { ReadLimit = limit });

    [Given(@"^a file server holding ""(.*)"" with ""(.*)"", on descriptor 3, whose reads fail with ""(.*)""$")]
    public Task GivenFailingFileServer(string file, string text, string error) => ServeAsync(file, text, new FileServerHarness { ReadRefusal = error });

    [Then("the file server was asked to open nothing")]
    public void ThenOpenedNothing() => Assert.Empty(server!.OpenModes);

    [Then("the file server was asked for no reads")]
    public void ThenNoReads() => Assert.Empty(server!.ReadTags);

    [Then(@"^the largest read the file server was asked for was (\d+) bytes$")]
    public void ThenLargestRead(int count) => Assert.Equal(count, server!.LargestData);

    // 9front's clock cannot be moved, so these run on Fog alone.
    [Given(@"^the clock moves on by (\d+) seconds$")]
    public void GivenClockMoves(int seconds)
    {
        fogOnly = true;
        clock.Now += TimeSpan.FromSeconds(seconds);
    }

    [Then(@"^the file server was attached as ""(.*)"" to ""(.*)""$")]
    public void ThenAttached(string user, string tree) => Assert.Contains((user, tree), server!.Attaches);

    [Given("rc starts with all of its 5000 descriptors in use")]
    public void GivenAllDescriptorsInUse() => (lastBusy, fogOnly) = (4999, true);

    [When("rc runs the script")]
    public Task WhenRuns(string text) => RunAsync(text, "/dev/null", [Script]);

    // 9front's console cannot be typed raw bytes, so these run on Fog alone.
    [When(@"^rc runs the script, its \\x escapes bytes$")]
    public Task WhenRunsBytes(string text)
    {
        fogOnly = true;
        return RunAsync(text, "/dev/null", [Script]);
    }

    [When("rc -i reads the script")]
    public Task WhenReadsInteractively(string text) => RunAsync(text, Script, ["-i"]);

    // 9front's console cannot be made to hold a read, so this runs on Fog alone.
    [When(@"^rc -i reads ""(.*)"" from a file server, and is interrupted as it waits to read$")]
    public async Task WhenInterruptedReading(string text)
    {
        fogOnly = true;
        await using var server = new FileServerHarness { HoldReads = true };
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("input", Encoding.UTF8.GetBytes(text + "\n")));
        var (client, served) = await init!.PipeAsync();
        server.Serve(init, served);
        await init.MountAsync(client, -1, "/n", MountFlags.Replace, string.Empty);
        await RunAsync(string.Empty, "/n/input", ["-i"], async pid =>
        {
            await server.RequestHeld(1).WaitAsync(Bound);
            int fd = await init.OpenAsync($"/proc/{pid}/note", NinePConstants.OWRITE);
            await init.WriteAsync(fd, "interrupt"u8.ToArray());
            await init.CloseAsync(fd);

            // The read the note interrupted is flushed before rc reads again.
            await server.RequestHeld(2).WaitAsync(Bound);
            server.Release();
        });
    }

    // 9front's console cannot be made to hold a write, so this runs on Fog alone.
    [When(@"^rc -i reads ""(.*)"" from a file server, and a hangup interrupts the write of its first prompt to the server$")]
    public async Task WhenInterruptedPrompting(string text)
    {
        fogOnly = true;
        await using var server = new FileServerHarness { HoldWrites = true };
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("input", Encoding.UTF8.GetBytes(text + "\n")));
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("prompt", []));
        var (client, served) = await init!.PipeAsync();
        server.Serve(init, served);
        await init.MountAsync(client, -1, "/n", MountFlags.Replace, string.Empty);
        async Task HangUpAsync(long pid)
        {
            await server.RequestHeld(1).WaitAsync(Bound);
            int fd = await init.OpenAsync($"/proc/{pid}/note", NinePConstants.OWRITE);
            await init.WriteAsync(fd, "hangup"u8.ToArray());
            await init.CloseAsync(fd);
            await server.HoldFlushed.WaitAsync(Bound);
            server.Release();
        }

        await RunAsync(string.Empty, "/n/input", ["-i"], HangUpAsync, "/n/prompt");
    }

    [Then("it prints nothing")]
    public Task ThenPrintsNothing() => CompareAsync(string.Empty);

    [Then("it prints")]
    public Task ThenPrints(string expected) => CompareAsync(expected + "\n");

    // rc -r prints each process's pid and each code vector's address, which differ from run to run,
    // and an address for the operations pfnc.c does not name.
    [Then("it traces")]
    public Task ThenTraces(string expected) => CompareAsync(expected + "\n", Mask);

    [Then("it prints, ending with a prompt")]
    public Task ThenPrintsPrompt(string expected) => CompareAsync(expected);

    // A here document's file is named for the process making it.
    private static string Here(string text) => Regex.Replace(text, "/tmp/here[0-9A-Za-z]+", "/tmp/hereN");

    // The GLOB byte before a pattern character is shown as \u0001.
    private static string Mask(string trace)
        => Regex.Replace(Regex.Replace(trace, @"pid \d+ cycle [0-9A-F]+ (\d+) [0-9A-F]{8}(?:[0-9A-F]{8})?(?= |$)", "pid N cycle C $1 F", RegexOptions.Multiline), @"pid \d+ cycle [0-9A-F]+", "pid N cycle C").Replace("\u0001", "\\u0001", StringComparison.Ordinal);

    // Text whose \xNN escapes stand for bytes, as rc's strings hold bytes one per char.
    private static string Bytes(string text)
        => Regex.Replace(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text)), @"\\x([0-9A-F]{2})", m => ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString());

    private async Task CompareAsync(string expected, Func<string, string>? mask = null)
    {
        mask ??= text => text;
        string? nineFront = null;
        if (!fogOnly && await NineFrontOracle.TerminalAsync() is { } terminal)
        {
            await terminal.RunAsync("cat >" + Script + " <<'EOF9'\n" + script + "\nEOF9");
            nineFront = await terminal.RunAsync(oracle);
        }

        string fog = Here(mask(output));
        nineFront = nineFront is null ? null : Here(mask(nineFront));
        Assert.True(
            fog == expected && (nineFront is null || nineFront == expected),
            $"expected:\n{expected}\nFog printed:\n{fog}\n9front printed:\n{nineFront ?? "(not run)"}");
    }

    // What happens while rc runs, given its pid.
    private async Task ServeAsync(string file, string text, FileServerHarness harness)
    {
        fogOnly = true;
        server = harness;
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile(file, Encoding.UTF8.GetBytes(text + "\n")));

        // Its pipe is 3 and 4, past the descriptors rc's output takes as the other scenarios do.
        int[] low = [await init!.OpenAsync("/dev/null", NinePConstants.OREAD), await init.OpenAsync("/dev/null", NinePConstants.OREAD), await init.OpenAsync("/dev/null", NinePConstants.OREAD)];
        var (client, end) = await init.PipeAsync();
        foreach (int fd in low)
        {
            await init.CloseAsync(fd);
        }

        server.Serve(init, end);
        served = client;
    }

    private async Task RunAsync(string text, string input, string[] arguments, Func<long, Task>? meanwhile = null, string? error = null)
    {
        script = text;
        oracle = $"@{{rfork En; cd /; /bin/rc {string.Join(' ', arguments)} <{input} >[2=1]}}";
        await WriteAsync(Script, text + "\n");
        var (read, write) = await init!.PipeAsync();
        long pid = init.Fork(RforkFlags.Fdg, async child =>
        {
            await child.DupAsync(write, 1);
            await child.DupAsync(error is null ? write : await child.OpenAsync(error, NinePConstants.OWRITE), 2);
            await child.CloseAsync(read);
            if (served >= 0)
            {
                await child.DupAsync(served, 3);
            }

            Assert.Equal(0, await child.OpenAsync(input, NinePConstants.OREAD));
            for (int fd = 3; fd <= lastBusy; fd++)
            {
                await child.OpenAsync("/dev/null", NinePConstants.OREAD);
            }

            await child.ExecAsync("/bin/rc", [name, .. arguments]);
        });
        await init.CloseAsync(write);
        Task acting = meanwhile?.Invoke(pid) ?? Task.CompletedTask;

        // A zero-length write reads as empty, so the output ends where the hung-up pipe refuses reads.
        using var run = new CancellationTokenSource(RunBound);
        var bytes = new List<byte>();
        while (await ReadAsync(read, run.Token) is { } block)
        {
            bytes.AddRange(block.ToArray());
            Assert.True(bytes.Count <= MaxOutput, "rc printed more than any scenario expects");
        }

        await acting.WaitAsync(Bound);
        await init.WaitAsync().WaitAsync(Bound);

        output = Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task<ReadOnlyMemory<byte>?> ReadAsync(int fd, CancellationToken run)
    {
        try
        {
            return await init!.ReadAsync(fd, 8192).AsTask().WaitAsync(Bound, run);
        }
        catch (SyscallException)
        {
            return null;
        }
    }

    private async Task WriteAsync(string path, string text)
    {
        int fd = await init!.CreateAsync(path, NinePConstants.OWRITE, 0b111_111_101);
        await init.WriteAsync(fd, Encoding.Latin1.GetBytes(Bytes(text)));
        await init.CloseAsync(fd);
    }
}
