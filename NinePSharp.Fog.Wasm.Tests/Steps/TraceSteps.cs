using System.Text;
using System.Text.RegularExpressions;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Wasm.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Wasm.Tests.Steps;

// The trace interpreter (Fixtures/trace) runs a script of Preview 1 calls given as descriptor 0
// and writes its trace to a pipe as descriptor 1; the descriptors a script uses are set up first.
[Binding]
public sealed class TraceSteps(WasmDriver driver)
{
    private const int Parked = 10;
    private readonly List<(string Path, int Mode, int Fd)> descriptors = [];
    private string trace = string.Empty;
    private string status = string.Empty;

    [Given(@"^the file ""(.*)"" holds ""(.*)""$")]
    public Task GivenFile(string path, string text) => WriteAsync(path, Encoding.UTF8.GetBytes(Unescape(text)));

    [Given(@"^the directory ""(.*)""$")]
    public async Task GivenDirectory(string path)
        => await driver.Init.CloseAsync(await driver.Init.CreateAsync(path, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));

    [Given(@"^the interpreter has ""(.*)"" open for (reading|writing|reading and writing) as descriptor (\d+)$")]
    public void GivenOpen(string path, string mode, int fd)
        => descriptors.Add((path, mode switch { "reading" => NinePConstants.OREAD, "writing" => NinePConstants.OWRITE, _ => NinePConstants.ORDWR }, fd));

    [When("the trace interpreter runs:")]
    public async Task WhenRuns(string script)
    {
        await WriteAsync("/bin/trace", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "trace.wasm")));
        await WriteAsync("/tmp/script", Encoding.UTF8.GetBytes(script + "\n"));
        var (first, second) = await driver.Init.PipeAsync();
        long child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            // Open everything above the numbers it will have, then move each to its number.
            var opened = new List<(int Fd, string Path, int Mode)> { (0, "/tmp/script", NinePConstants.OREAD) };
            opened.AddRange(descriptors.Select(d => (d.Fd, d.Path, d.Mode)));
            for (int i = 0; i < opened.Count; i++)
            {
                int fd = await process.OpenAsync(opened[i].Path, opened[i].Mode);
                await process.RenumberAsync(fd, Parked + i);
            }

            await process.RenumberAsync(first, Parked + opened.Count);
            await process.CloseAsync(second);
            await process.RenumberAsync(Parked + opened.Count, 1);
            for (int i = 0; i < opened.Count; i++)
            {
                await process.RenumberAsync(Parked + i, opened[i].Fd);
            }

            await process.ExecAsync("/bin/trace", ["trace"]);
        });
        await driver.Init.CloseAsync(first);
        var output = new StringBuilder();
        Task<Waitmsg> wait = driver.Init.WaitAsync().WaitAsync(WasmDriver.Bound);
        for (ReadOnlyMemory<byte> block; !(block = await driver.Init.ReadAsync(second, 8192).AsTask().WaitAsync(WasmDriver.Bound)).IsEmpty;)
        {
            output.Append(Encoding.UTF8.GetString(block.Span));
        }

        await driver.Init.CloseAsync(second);
        Waitmsg ended = await wait;
        Assert.Equal(child, ended.Pid);
        trace = output.ToString();
        status = ended.Message;
    }

    // Each line as written, where * stands for anything.
    [Then("the trace is:")]
    public void ThenTrace(string expected)
    {
        string[] want = expected.Split('\n');
        string[] got = trace.TrimEnd('\n').Split('\n');
        bool matches = want.Length == got.Length
            && want.Zip(got).All(line => Regex.IsMatch(line.Second, "^" + Regex.Escape(line.First).Replace("\\*", ".*", StringComparison.Ordinal) + "$"));
        Assert.True(matches, $"the interpreter ended \"{status}\" after the trace:\n{trace}");
    }

    [Then(@"^the trace has (\d+) lines, the first two of (\d+) random bytes that differ$")]
    public void ThenRandom(int lines, int bytes)
    {
        string[] got = trace.TrimEnd('\n').Split('\n');
        string[] draws = [.. got.Take(2).Select(line => Regex.Unescape(line[(line.IndexOf("-> ok \"", StringComparison.Ordinal) + 7)..^1]))];
        Assert.Equal(lines, got.Length);
        Assert.All(draws, draw => Assert.Equal(bytes, draw.Length));
        Assert.NotEqual(draws[0], draws[1]);
    }

    [Then(@"^its last line is ""(.*)""$")]
    public void ThenLastLine(string line) => Assert.Equal(line, trace.TrimEnd('\n').Split('\n')[^1]);

    [Then(@"^descriptors (\d+) and (\d+) report one inode and descriptor (\d+) another$")]
    public void ThenInodes(int fd, int same, int other)
    {
        string Inode(int descriptor) => Regex.Match(trace, $@"^fd_filestat_get {descriptor} -> ok dev=0 ino=(\d+) ", RegexOptions.Multiline).Groups[1].Value;
        Assert.Equal(Inode(fd), Inode(same));
        Assert.NotEqual(Inode(fd), Inode(other));
        Assert.NotEmpty(Inode(other));
    }

    [Then(@"^the file ""(.*)"" holds ""(.*)""$")]
    public async Task ThenFileHolds(string path, string text)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(Unescape(text), Encoding.UTF8.GetString((await driver.Init.ReadAsync(fd, 65536)).Span));
        await driver.Init.CloseAsync(fd);
    }

    private static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal);

    private async Task WriteAsync(string path, byte[] contents)
    {
        int fd = await driver.Init.CreateAsync(path, NinePConstants.ORDWR, 0b111_111_101);
        await driver.Init.WriteAsync(fd, contents);
        await driver.Init.CloseAsync(fd);
    }
}
