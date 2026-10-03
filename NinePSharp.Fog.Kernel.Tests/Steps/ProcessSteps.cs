using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "Programs run as Plan 9 processes on Fog's namespace")]
public sealed class ProcessSteps
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private readonly List<string> seen = new();
    private readonly OpenCounter files = new();
    private Process? init;
    private Waitmsg? waited;
    private long child;
    private string? failure;
    private string? childRead;
    private IReadOnlyList<string>? childListed;

    [Given(@"^a kernel whose /bin holds the managed programs ""true"", ""false"", ""args"", ""return"" and ""crash""$")]
    public async Task GivenAKernel()
    {
        var kernel = new FogKernel(
            new Dictionary<string, ProgramMain>
        {
            ["true"] = (process, argv) =>
            {
                process.Exits(null);
                return Task.CompletedTask;
            },
            ["false"] = (process, argv) =>
            {
                process.Exits("false");
                return Task.CompletedTask;
            },
            ["args"] = (process, argv) =>
            {
                seen.AddRange(argv);
                process.Exits(null);
                return Task.CompletedTask;
            },
            ["return"] = (process, argv) => Task.CompletedTask,
            ["crash"] = (process, argv) => throw new InvalidOperationException("boom"),
        },
            files,
            files.Root);
        init = await kernel.BootAsync();
    }

    [Given(@"^the file ""(.*)"" holds ""(.*)""$")]
    public Task GivenFileHolds(string path, string contents)
        => WriteAsync(path, contents.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\0", "\0", StringComparison.Ordinal));

    [Given(@"^the file ""(.*)"" runs ""(.*)"" with (\d+) interpreter arguments$")]
    public Task GivenInterpreterArguments(string path, string interpreter, int count)
        => WriteAsync(path, $"#!{interpreter}{string.Concat(Enumerable.Repeat(" a", count))}\n");

    [Given(@"^nine scripts ""/tmp/s1"" to ""/tmp/s9"", each naming the next as its interpreter, and ""/tmp/s9"" naming ""/bin/true""$")]
    public async Task GivenNineScripts()
    {
        for (int i = 1; i < 9; i++)
        {
            await WriteAsync($"/tmp/s{i}", $"#!/tmp/s{i + 1}\n");
        }

        await WriteAsync("/tmp/s9", "#!/bin/true\n");
    }

    [Given(@"^the directory ""(.*)"" holds the file ""(.*)""$")]
    public async Task GivenDirectoryHolds(string directory, string name)
    {
        await init!.CloseAsync(await init.CreateAsync(directory, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0775));
        await WriteAsync($"{directory}/{name}", string.Empty);
    }

    [Given(@"^a process with ""(.*)"" open as descriptor 3$")]
    public async Task GivenOpenAsThree(string path)
    {
        int fd = await init!.OpenAsync(path, NinePConstants.ORDWR);
        Assert.Equal(3, await init.DupAsync(fd, 3));
        await init.CloseAsync(fd);
    }

    [Given(@"^a process with ""(.*)"" open as descriptor 3 with OCEXEC$")]
    public async Task GivenOpenAsThreeCloseOnExec(string path)
    {
        for (int fd = 0; fd < 4; fd++)
        {
            Assert.Equal(fd, await init!.OpenAsync(path, NinePConstants.ORDWR | NinePConstants.OCEXEC));
        }

        for (int fd = 0; fd < 3; fd++)
        {
            await init!.CloseAsync(fd);
        }
    }

    [Given("descriptor 3 has been read to the end")]
    public async Task GivenReadToEnd() => Assert.False((await init!.ReadAsync(3, 8192)).IsEmpty);

    [Given(@"^a process that writes ""(.*)"" to ""(.*)""$")]
    public Task GivenProcessWrites(string contents, string path) => WriteAsync(path, contents);

    [When(@"^a process runs ""([^""]*)""$")]
    public Task WhenRuns(string path) => RunAsync(path, Array.Empty<string>());

    [When(@"^a process runs ""(.*)"" with arguments ""(.*)""$")]
    public Task WhenRunsWithArguments(string path, string arguments) => RunAsync(path, arguments.Split(' '));

    [When(@"^a process forks a child that exits with ""(.*)""$")]
    public async Task WhenForksExiting(string status)
    {
        child = init!.Fork(RforkFlags.Fdg, process =>
        {
            process.Exits(status);
            return Task.CompletedTask;
        });
        waited = await init.WaitAsync().WaitAsync(Bound);
    }

    [When("a process forks a child that returns")]
    public async Task WhenForksReturning()
    {
        child = init!.Fork(RforkFlags.Fdg, process => Task.CompletedTask);
        waited = await init.WaitAsync().WaitAsync(Bound);
    }

    [When("it closes descriptor 3")]
    public Task WhenClosesThree() => init!.CloseAsync(3).AsTask();

    [When("a process forks a child with RFENVG and RFCENVG")]
    public void WhenForksWithBoth()
        => failure = Assert.Throws<SyscallException>(() => init!.Fork(RforkFlags.Envg | RforkFlags.Cenvg, process => Task.CompletedTask)).Message;

    [When(@"^it forks a child (with|without) (RF\w+) that closes descriptor 3$")]
    public Task WhenForksClosing(string with, string flag) => ForkAsync(with, flag, child => child.CloseAsync(3).AsTask());

    [When(@"^it forks a child (with|without) (RF\w+) that reads ""(.*)""$")]
    public Task WhenForksReading(string with, string flag, string path)
        => ForkAsync(with, flag, async child => childRead = await ReadAsync(child, path));

    [When(@"^it forks a child (with|without) (RF\w+) that writes ""(.*)"" to ""(.*)""$")]
    public Task WhenForksWriting(string with, string flag, string contents, string path)
        => ForkAsync(with, flag, async child =>
        {
            int fd = await child.CreateAsync(path, NinePConstants.OWRITE, 0666);
            await child.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
            await child.CloseAsync(fd);
        });

    [When(@"^it forks a child (with|without) (RF\w+) that lists ""(.*)""$")]
    public Task WhenForksListing(string with, string flag, string path)
        => ForkAsync(with, flag, async child =>
        {
            int fd = await child.OpenAsync(path, NinePConstants.OREAD);
            childListed = (await child.DirReadAsync(fd)).Select(entry => entry.Name!).ToArray();
        });

    [When(@"^it forks a child (with|without) (RF\w+) that runs ""(.*)""$")]
    public Task WhenForksRunning(string with, string flag, string path)
        => ForkAsync(with, flag, child => child.ExecAsync(path, [path]));

    [When("a process with no children waits")]
    public Task WhenWaitsWithoutChildren() => WaitFailsAsync();

    [When("it waits again")]
    public Task WhenWaitsAgain() => WaitFailsAsync();

    [When(@"^a process (opens|creates|removes|enters) ""(.*)""$")]
    public async Task WhenCalls(string call, string path)
    {
        try
        {
            switch (call)
            {
                case "opens":
                    await init!.OpenAsync(path, NinePConstants.OREAD);
                    break;
                case "creates":
                    await init!.CreateAsync(path, NinePConstants.OWRITE, 0666);
                    break;
                case "removes":
                    await init!.RemoveAsync(path);
                    break;
                default:
                    await init!.ChdirAsync(path);
                    break;
            }
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When(@"^a process (reads|writes|closes|duplicates) descriptor 7$")]
    public async Task WhenUsesDescriptor(string call)
    {
        Func<Task> use = call switch
        {
            "reads" => () => init!.ReadAsync(7, 1).AsTask(),
            "writes" => () => init!.WriteAsync(7, new byte[1]).AsTask(),
            "closes" => () => init!.CloseAsync(7).AsTask(),
            _ => () => init!.DupAsync(7, -1).AsTask(),
        };
        failure = (await Assert.ThrowsAsync<SyscallException>(use)).Message;
    }

    [When(@"^a process writes ""(.*)"" at the start of ""(.*)""$")]
    public async Task WhenWritesAtStart(string contents, string path)
    {
        int fd = await init!.OpenAsync(path, NinePConstants.OWRITE);
        await init.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
        await init.CloseAsync(fd);
    }

    [When(@"^a process opens ""(.*)"" with OTRUNC$")]
    public async Task WhenOpensTruncating(string path)
        => await init!.CloseAsync(await init.OpenAsync(path, NinePConstants.OWRITE | NinePConstants.OTRUNC));

    [Then("its parent's wait message for it is empty")]
    public void ThenWaitMessageEmpty()
    {
        Assert.Equal(child, waited!.Pid);
        Assert.Equal(string.Empty, waited.Message);
    }

    [Then(@"^its parent's wait message for it is ""(.*)""$")]
    public void ThenWaitMessage(string message)
    {
        Assert.Equal(child, waited!.Pid);
        Assert.Equal(message.Replace("<pid>", child.ToString(), StringComparison.Ordinal), waited.Message);
    }

    [Then(@"^the program saw the arguments ""(.*)""$")]
    public void ThenSawArguments(string arguments) => Assert.Equal(arguments, string.Join(' ', seen));

    [Then(@"^the program saw (\d+) arguments$")]
    public void ThenSawCount(int count) => Assert.Equal(count, seen.Count);

    [Then(@"^exec fails with ""(.*)""$")]
    public void ThenExecFails(string message)
    {
        Assert.Equal(message, failure);
        Assert.Empty(seen);
    }

    [Then(@"^(?:wait|it) fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);

    [Then("descriptor 3 is still open in the parent")]
    public async Task ThenStillOpen() => Assert.Equal("d", Encoding.UTF8.GetString((await init!.ReadAsync(3, 1)).Span));

    [Then("descriptor 3 is closed in the parent")]
    public async Task ThenClosed()
        => Assert.Equal("fd out of range or not open", (await Assert.ThrowsAsync<SyscallException>(() => init!.ReadAsync(3, 1).AsTask())).Message);

    [Then(@"^the child read ""(.*)""$")]
    public void ThenChildRead(string contents) => Assert.Equal(contents, childRead);

    [Then(@"^the parent reads ""(.*)"" from ""(.*)""$")]
    public async Task ThenParentReads(string contents, string path) => Assert.Equal(contents, await ReadAsync(init!, path));

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReadingGives(string path, string contents) => Assert.Equal(contents, await ReadAsync(init!, path));

    [Then(@"^opening ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenOpeningFails(string path, string message)
        => Assert.Equal(message, (await Assert.ThrowsAsync<SyscallException>(() => init!.OpenAsync(path, NinePConstants.OREAD).AsTask())).Message);

    [Then(@"^reading descriptor 3 gives ""(.*)""$")]
    public async Task ThenReadingThreeGives(string contents)
        => Assert.Equal(contents, Encoding.UTF8.GetString((await init!.ReadAsync(3, 8192)).Span));

    [Then(@"^reading descriptor 3 two bytes at a time gives ""(.*)"", ""(.*)"", ""(.*)"" and then nothing$")]
    public async Task ThenReadingInPieces(string first, string second, string third)
    {
        foreach (string piece in new[] { first, second, third, string.Empty })
        {
            Assert.Equal(piece, Encoding.UTF8.GetString((await init!.ReadAsync(3, 2)).Span));
        }
    }

    [Then(@"^the parent lists ""(.*)"" in ""(.*)""$")]
    public async Task ThenParentLists(string names, string path)
    {
        int fd = await init!.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(names, string.Join(' ', (await init.DirReadAsync(fd)).Select(entry => entry.Name)));
        await init.CloseAsync(fd);
    }

    [Then("no file is left open")]
    public void ThenNothingOpen() => Assert.Equal(0, files.Open);

    [Then("the child listed nothing")]
    public void ThenListedNothing() => Assert.Empty(childListed!);

    [Then(@"^the child listed ""(.*)""$")]
    public void ThenListed(string names) => Assert.Equal(names, string.Join(' ', childListed!));

    private static async Task<string> ReadAsync(Process process, string path)
    {
        int fd = await process.OpenAsync(path, NinePConstants.OREAD);
        string contents = Encoding.UTF8.GetString((await process.ReadAsync(fd, 8192)).Span);
        await process.CloseAsync(fd);
        return contents;
    }

    private async Task WriteAsync(string path, string contents)
    {
        int fd = await init!.CreateAsync(path, NinePConstants.OWRITE, 0775);
        await init.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
        await init.CloseAsync(fd);
    }

    private async Task WaitFailsAsync()
        => failure = (await Assert.ThrowsAsync<SyscallException>(() => init!.WaitAsync().WaitAsync(Bound))).Message;

    private async Task RunAsync(string path, string[] arguments)
    {
        child = init!.Fork(RforkFlags.Fdg, async process =>
        {
            try
            {
                await process.ExecAsync(path, [path, .. arguments]);
            }
            catch (SyscallException error)
            {
                failure = error.Message;
                process.Exits(error.Message);
            }

            process.Exits("exec returned");
        });
        waited = await init.WaitAsync().WaitAsync(Bound);
    }

    private async Task ForkAsync(string with, string flag, Func<Process, Task> body)
    {
        RforkFlags flags = with == "with" ? Enum.Parse<RforkFlags>(flag[2..], ignoreCase: true) : 0;
        child = init!.Fork(flags, async process =>
        {
            await body(process);
            process.Exits(null);
        });
        waited = await init.WaitAsync().WaitAsync(Bound);
        Assert.Equal(string.Empty, waited.Message);
    }
}
