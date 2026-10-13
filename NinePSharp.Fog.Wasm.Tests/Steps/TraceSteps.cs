using System.Text;
using System.Text.RegularExpressions;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Wasm.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
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
    private Func<Process, Task>? change;
    private string trace = string.Empty;
    private string status = string.Empty;
    private TimeSpan took;

    [Given(@"^the file ""(.*)"" holds ""(.*)""$")]
    public Task GivenFile(string path, string text) => WriteAsync(path, Encoding.UTF8.GetBytes(Unescape(text)));

    [Given(@"^the (read|write)-only file ""(.*)"" holds ""(.*)""$")]
    public async Task GivenOneWayFile(string way, string path, string text)
    {
        int fd = await driver.Init.CreateAsync(path, NinePConstants.OWRITE, way == "read" ? 0b100_100_100 : 0b010_010_010u);
        await driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text));
        await driver.Init.CloseAsync(fd);
    }

    [Given(@"^the directory ""(.*)""$")]
    public Task GivenDirectory(string path) => MakeDirectoriesAsync(path);

    [Given(@"^the interpreter has ""(.*)"" open for (reading|writing|reading and writing) as descriptor (\d+)$")]
    public void GivenOpen(string path, string mode, int fd)
        => descriptors.Add((path, mode switch { "reading" => NinePConstants.OREAD, "writing" => NinePConstants.OWRITE, _ => NinePConstants.ORDWR }, fd));

    [Given(@"^""(.*)"" is bound (before|after) ""(.*)""$")]
    public async Task GivenUnion(string name, string where, string old)
        => await driver.Init.BindAsync(name, old, where == "before" ? MountFlags.Before : MountFlags.After);

    // The interpreter writes to descriptor 7 when it is ready and waits on descriptor 6; between, the
    // parent, which shares its namespace, makes the change, then writes "go" and closes its end.
    [Given("once the interpreter is ready, the parent writes to descriptor 6 and closes its end")]
    public void GivenWrite() => change = process => Task.CompletedTask;

    [Given(@"^once the interpreter is ready, the parent binds ""(.*)"" over ""(.*)""$")]
    public void GivenBind(string name, string old) => change = process => process.BindAsync(name, old, MountFlags.Replace).AsTask();

    [Given(@"^once the interpreter is ready, the parent removes ""(.*)"" and then ""(.*)"" and makes a new one holding ""(.*)""$")]
    public void GivenReplace(string inside, string path, string file) => change = async process =>
    {
        await process.RemoveAsync(inside);
        await process.RemoveAsync(path);
        await process.CloseAsync(await process.CreateAsync(path, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));
        await process.CloseAsync(await process.CreateAsync($"{path}/{file}", NinePConstants.OWRITE, 0b110_110_110));
    };

    [Given(@"^once the interpreter is ready, the parent sets the modification time of ""(.*)"" to (\d+)$")]
    public void GivenTouch(string path, uint mtime) => change = async process =>
    {
        Stat none = Process.NullDir;
        await process.WStatAsync(path, new Stat(0, none.Type, none.Dev, none.Qid, none.Mode, none.Atime, mtime, none.Length, none.Name, none.Uid, none.Gid, none.Muid));
    };

    [Given(@"^once the interpreter is ready, the parent renames ""(.*)"" to ""(.*)""$")]
    public void GivenRename(string path, string to) => change = async process =>
    {
        Stat none = Process.NullDir;
        await process.WStatAsync(path, new Stat(0, none.Type, none.Dev, none.Qid, none.Mode, none.Atime, none.Mtime, none.Length, to[(to.LastIndexOf('/') + 1)..], none.Uid, none.Gid, none.Muid));
    };

    [When("the trace interpreter runs:")]
    public async Task WhenRuns(string script)
    {
        await WriteAsync("/bin/trace", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "trace.wasm")));
        await WriteAsync("/tmp/script", Encoding.UTF8.GetBytes(script + "\n"));
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (first, second) = await driver.Init.PipeAsync();
        (int Fd, int Other) ready = change is null ? (-1, -1) : await driver.Init.PipeAsync();
        (int Fd, int Other) resume = change is null ? (-1, -1) : await driver.Init.PipeAsync();
        long child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            // Open everything above the numbers it will have, then move each to its number.
            var opened = new List<(int Fd, string Path, int Mode)> { (0, "/tmp/script", NinePConstants.OREAD) };
            opened.AddRange(descriptors.Select(d => (d.Fd, d.Path, d.Mode)));
            if (change is not null)
            {
                await process.RenumberAsync(ready.Fd, 7);
                await process.RenumberAsync(resume.Fd, 6);
                await process.CloseAsync(ready.Other);
                await process.CloseAsync(resume.Other);
            }

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
        if (change is not null)
        {
            await driver.Init.CloseAsync(ready.Fd);
            await driver.Init.CloseAsync(resume.Fd);
            await driver.Init.ReadAsync(ready.Other, 16).AsTask().WaitAsync(WasmDriver.Bound);
            await change(driver.Init);
            await driver.Init.WriteAsync(resume.Other, "go"u8.ToArray());
            await driver.Init.CloseAsync(resume.Other);
        }

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
        took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
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

    [Then(@"^""(.*)"" does not exist$")]
    public async Task ThenMissing(string path) => await Assert.ThrowsAsync<SyscallException>(async () => await driver.Init.StatAsync(path));

    [Then(@"^fd_readdir gives ""\."" the inode fd_filestat_get gives descriptor 3, and ""report"" the one path_filestat_get gives it$")]
    public void ThenReaddirInodes()
    {
        string Dirent(string name) => Regex.Match(trace, $"\"{Regex.Escape(name)}\":\\d+:\\d+:(\\d+)").Groups[1].Value;
        string Stat(string call) => Regex.Match(trace, $"^{Regex.Escape(call)} -> ok dev=0 ino=(\\d+) ", RegexOptions.Multiline).Groups[1].Value;
        Assert.Equal(Stat("fd_filestat_get 3"), Dirent("."));
        Assert.Equal(Stat("path_filestat_get 3 0 \"report\""), Dirent("report"));
        Assert.NotEqual(Dirent("."), Dirent("report"));
    }

    [Then(@"^the file ""(.*)"" was last modified at (\d+)$")]
    public async Task ThenModified(string path, uint mtime) => Assert.Equal(mtime, (await driver.Init.StatAsync(path)).Mtime);

    [Then(@"^the run took at least (\d+) ms$")]
    public void ThenTook(int milliseconds) => Assert.True(took >= TimeSpan.FromMilliseconds(milliseconds), $"the run took {took}");

    [Then(@"^the file ""(.*)"" holds ""(.*)""$")]
    public async Task ThenFileHolds(string path, string text)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(Unescape(text), Encoding.UTF8.GetString((await driver.Init.ReadAsync(fd, 65536)).Span));
        await driver.Init.CloseAsync(fd);
    }

    private static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal);

    // The directory and any missing above it.
    private async Task MakeDirectoriesAsync(string path)
    {
        for (int slash = path.IndexOf('/', 1); ; slash = path.IndexOf('/', slash + 1))
        {
            string directory = slash < 0 ? path : path[..slash];
            try
            {
                await driver.Init.StatAsync(directory);
            }
            catch (SyscallException)
            {
                await driver.Init.CloseAsync(await driver.Init.CreateAsync(directory, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));
            }

            if (slash < 0)
            {
                return;
            }
        }
    }

    private async Task WriteAsync(string path, byte[] contents)
    {
        await MakeDirectoriesAsync(path[..path.LastIndexOf('/')]);
        int fd = await driver.Init.CreateAsync(path, NinePConstants.ORDWR, 0b111_111_101);
        await driver.Init.WriteAsync(fd, contents);
        await driver.Init.CloseAsync(fd);
    }
}
