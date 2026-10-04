using System.Text;
using System.Text.RegularExpressions;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests.Steps;

[Binding]
[Scope(Feature = "rc runs scripts as 9front rc does")]
public sealed class RcExecutionSteps
{
    private const string Script = "/tmp/s";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);
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

    [Given(@"^a kernel whose /bin holds rc, echo and cat$")]
    public async Task GivenAKernel()
    {
        var programs = new Dictionary<string, ProgramMain>(Commands.Commands.All) { ["rc"] = RcProgram.MainAsync };
        init = await FogKernel.InMemory(programs, "glenda").BootAsync();
        await init.CloseAsync(await init.CreateAsync("/rc", NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));
        await init.CloseAsync(await init.CreateAsync("/rc/lib", NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));
        await WriteAsync("/rc/lib/rcmain", RcProgram.Rcmain);
    }

    [Given("rc starts with all but two of its 5000 descriptors in use")]
    public void GivenDescriptorsInUse() => (lastBusy, fogOnly) = (4997, true);

    [Given("rc is started as a login shell")]
    public void GivenLogin() => (name, fogOnly) = ("-rc", true);

    [Given("rc starts with all but one of its 5000 descriptors in use")]
    public void GivenAllButOneInUse() => (lastBusy, fogOnly) = (4998, true);

    [Given(@"^""(.*)"" holds ""(.*)""$")]
    public Task GivenHolds(string path, string text) => WriteAsync(path, text);

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

    private async Task RunAsync(string text, string input, string[] arguments)
    {
        script = text;
        oracle = $"@{{rfork En; cd /; /bin/rc {string.Join(' ', arguments)} <{input} >[2=1]}}";
        await WriteAsync(Script, text + "\n");
        var (read, write) = await init!.PipeAsync();
        init.Fork(RforkFlags.Fdg, async child =>
        {
            await child.DupAsync(write, 2);
            await child.CloseAsync(read);
            Assert.Equal(0, await child.OpenAsync(input, NinePConstants.OREAD));
            for (int fd = 3; fd <= lastBusy; fd++)
            {
                await child.OpenAsync("/dev/null", NinePConstants.OREAD);
            }

            await child.ExecAsync("/bin/rc", [name, .. arguments]);
        });
        await init.CloseAsync(write);

        // A zero-length write reads as empty, so the output ends where the hung-up pipe refuses reads.
        var bytes = new List<byte>();
        while (await ReadAsync(read) is { } block)
        {
            bytes.AddRange(block.ToArray());
        }

        await init.WaitAsync().WaitAsync(Bound);
        output = Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task<ReadOnlyMemory<byte>?> ReadAsync(int fd)
    {
        try
        {
            return await init!.ReadAsync(fd, 8192).AsTask().WaitAsync(Bound);
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
