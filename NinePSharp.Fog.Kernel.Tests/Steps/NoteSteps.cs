using System.Globalization;
using System.Text;
using System.Threading.Channels;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "Notes interrupt processes, and /proc posts them, as 9front's postnote and devproc do")]
public sealed class NoteSteps(KernelDriver driver) : IAsyncDisposable
{
    private const string Owner = "glenda";
    private readonly TaskCompletionSource<bool> holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Agent> agents = [];
    private readonly List<int> pipes = [];
    private readonly List<string?> writes = [];
    private Agent? child;
    private Agent? grandchild;
    private Task? blocked;
    private Task? childCall;
    private string? failure;

    private Process Init => driver.Init;

    [Given(@"^a booted kernel whose /bin holds the managed program ""hold""$")]
    public async Task GivenKernel()
    {
        var programs = new Dictionary<string, ProgramMain>
        {
            ["hold"] = (_, _) =>
            {
                held.TrySetResult(true);
                return holding.Task;
            },
        };
        driver.Kernel = FogKernel.InMemory(programs, Owner, driver.Clock);
        driver.Init = await driver.Kernel.BootAsync();
    }

    [Given("a child blocked reading an empty pipe")]
    public Task GivenBlocked() => BlockedReadingAsync(null);

    [Given("a child that continues after every note, blocked reading an empty pipe")]
    public Task GivenContinuingBlocked() => BlockedReadingAsync(true);

    [Given("a child that leaves every note to its default, blocked reading an empty pipe")]
    public Task GivenDefaultBlocked() => BlockedReadingAsync(false);

    [Given(@"^(\d+) children that continue after every note, blocked reading an empty pipe$")]
    public async Task GivenChildrenBlocked(int count)
    {
        int read = await PipeAsync();
        for (int i = 0; i < count; i++)
        {
            Agent agent = await StartAsync(Init, true);
            _ = agent.Do(p => p.ReadAsync(read, 1).AsTask());
        }
    }

    [Given("a child that continues after every note, blocked waiting for a child of its own")]
    public async Task GivenBlockedWaiting()
    {
        child = await StartAsync(Init, true);
        grandchild = await child.Do(p => StartAsync(p, null));
        blocked = child.Do(p => p.WaitAsync());
    }

    [Given("a child that continues after every note, held before its next call")]
    public async Task GivenContinuingHeld() => child = await StartAsync(Init, true);

    [Given("a child held before its next call")]
    public async Task GivenHeld() => child = await StartAsync(Init, null);

    [Given("a child in a note group of its own that continues after every note, held before its next call")]
    public async Task GivenOwnGroup()
    {
        child = await StartAsync(Init, true);
        await child.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());
    }

    [Given("a child that continues after every note, holding the write end of a pipe whose read end is closed")]
    public Task GivenContinuingWriter() => WriterAsync(true);

    [Given("a child holding the write end of a pipe whose read end is closed")]
    public Task GivenWriter() => WriterAsync(null);

    [Given(@"^a child that runs ""(.*)"" with arguments ""(.*)""$")]
    public async Task GivenRuns(string path, string arguments)
    {
        child = await StartAsync(Init, null);
        string elem = path[(path.LastIndexOf('/') + 1)..];
        _ = child.Do(p => p.ExecAsync(path, [elem, .. Process.Tokenize(arguments, 64)]));
        await Bounded(held.Task);
    }

    [Given(@"^a child that becomes ""none"" and forks a grandchild that continues after every note, held before its next call$")]
    public async Task GivenNoneChild()
    {
        child = await StartAsync(Init, true);
        await child.Do(async p =>
        {
            int fd = await p.OpenAsync("/dev/user", NinePConstants.OWRITE);
            await p.WriteAsync(fd, "none"u8.ToArray());
            await p.CloseAsync(fd);
        });
        grandchild = await child.Do(p => StartAsync(p, true));
    }

    [When(@"^the process writes ""(.*)"" to the child's (note|args|noteid)$")]
    public Task WhenWritesChild(string text, string file) => RecordAsync(Init, $"/proc/{child!.Pid}/{file}", text);

    [When(@"^the process writes ""(.*)"" to its own notepg$")]
    public Task WhenWritesNotepg(string text) => RecordAsync(Init, $"/proc/{Init.Pid}/notepg", text);

    [When(@"^the process writes (\d+) notes to the child's note$")]
    public async Task WhenWritesNotes(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await RecordAsync(Init, $"/proc/{child!.Pid}/note", $"note {i}");
        }
    }

    [When(@"^the process writes a note of (\d+) bytes to the child's note$")]
    public Task WhenWritesLongNote(int length) => RecordAsync(Init, $"/proc/{child!.Pid}/note", new string('n', length));

    [When("the process writes its own note id to the child's noteid")]
    public async Task WhenJoins() => await RecordAsync(Init, $"/proc/{child!.Pid}/noteid", (await NoteIdAsync(Init.Pid)).ToString(CultureInfo.InvariantCulture));

    [When("the process writes the child's pid to the child's noteid")]
    public Task WhenOwnPid() => RecordAsync(Init, $"/proc/{child!.Pid}/noteid", child.Pid.ToString(CultureInfo.InvariantCulture));

    [When("the process opens its own notepg for reading")]
    public async Task WhenOpensNotepg()
    {
        try
        {
            await Init.OpenAsync($"/proc/{Init.Pid}/notepg", NinePConstants.OREAD);
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When("the child has exited")]
    public async Task WhenExited() => await Bounded(Init.WaitAsync());

    [When(@"^the child is let go to stat ""(.*)""$")]
    public void WhenLetGo(string path) => childCall = child!.Do(p => p.StatAsync(path).AsTask());

    [When("the child writes to the pipe")]
    public void WhenChildWrites() => childCall = child!.Do(p => p.WriteAsync(pipes[^1], "x"u8.ToArray()).AsTask());

    [When(@"^the child writes ""(.*)"" to the grandchild's note$")]
    public Task WhenChildWritesGrandchild(string text) => ChildRecordAsync($"/proc/{grandchild!.Pid}/note", text);

    [When(@"^the child writes ""(.*)"" to its own notepg$")]
    public Task WhenChildWritesNotepg(string text) => ChildRecordAsync($"/proc/{child!.Pid}/notepg", text);

    [When("the child makes a note group of its own")]
    public Task WhenChildNewGroup() => child!.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());

    [Then(@"^""(.*)"" is a directory of device ""(.)""$")]
    public async Task ThenDevice(string path, char device)
    {
        var stat = await Init.StatAsync(path);
        Assert.Equal((device, true), ((char)stat.Type, (stat.Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0));
    }

    [Then(@"^the names in the process's own /proc directory are ""(.*)""$")]
    public async Task ThenOwnNames(string names) => Assert.Equal(names, string.Join(' ', await NamesAsync($"/proc/{Init.Pid}")));

    [Then(@"^/proc lists the process and the child$")]
    public async Task ThenListsBoth() => Assert.Equal(new[] { Init.Pid, child!.Pid }.Order(), (await NamesAsync("/proc")).Select(long.Parse).Order());

    [Then(@"^/proc lists only the process$")]
    public async Task ThenListsInit() => Assert.Equal([Init.Pid], (await NamesAsync("/proc")).Select(long.Parse));

    [Then(@"^the child's (read|wait|write) fails with ""(.*)""$")]
    public async Task ThenChildFails(string call, string message)
    {
        if (call == "write" && childCall is null)
        {
            Assert.Equal(message, writes[^1]);
            return;
        }

        Task task = call == "write" ? childCall! : blocked!;
        Assert.Equal(message, (await Assert.ThrowsAsync<SyscallException>(() => Bounded(task))).Message);
    }

    [Then("the child's stat succeeds")]
    public Task ThenStatSucceeds() => Bounded(childCall!);

    [Then(@"^the child's handler saw ""(.*)""$")]
    public Task ThenSaw(string note) => child!.SawAsync(note);

    [Then(@"^each child's handler saw ""(.*)""$")]
    public async Task ThenEachSaw(string note)
    {
        foreach (Agent agent in agents.Where(a => a.Parent == Init))
        {
            await agent.SawAsync(note);
        }
    }

    [Then(@"^the child's wait message is ""\*init\* <pid>: (.*)""$")]
    public async Task ThenWaitMessage(string status)
    {
        Waitmsg waited = await Bounded(Init.WaitAsync());
        Assert.Equal((child!.Pid, $"*init* {child.Pid}: {status}"), (waited.Pid, waited.Message));
    }

    [Then(@"^the first (\d+) writes succeed and the sixth fails with ""(.*)""$")]
    public void ThenFirstSucceed(int count, string message)
        => Assert.Equal([.. Enumerable.Repeat<string?>(null, count), message], writes);

    [Then(@"^the write fails with ""(.*)""$")]
    public void ThenWriteFails(string message) => Assert.Equal(message, writes[^1]);

    [Then("the child's write succeeds")]
    public void ThenChildWriteSucceeds() => Assert.Null(writes[^1]);

    [Then(@"^reading the (child|grandchild)'s note gives ""(.*)""$")]
    public async Task ThenNoteGives(string who, string note) => Assert.Equal(note, await ReadNoteAsync((who == "child" ? child! : grandchild!).Pid));

    [Then("reading the child's note gives nothing")]
    public async Task ThenNoteGivesNothing() => Assert.Equal(string.Empty, await ReadNoteAsync(child!.Pid));

    [Then("no note waits for the process")]
    public async Task ThenNoNoteForInit() => Assert.Equal(string.Empty, await ReadNoteAsync(Init.Pid));

    [Then("no note waits for the child")]
    public async Task ThenNoNoteForChild() => Assert.Equal(string.Empty, await ReadNoteAsync(child!.Pid));

    [Then("the child's note id is not the process's")]
    public async Task ThenOtherGroup() => Assert.NotEqual(await NoteIdAsync(Init.Pid), await NoteIdAsync(child!.Pid));

    [Then("the child's note id is the process's")]
    public async Task ThenSameGroup() => Assert.Equal(await NoteIdAsync(Init.Pid), await NoteIdAsync(child!.Pid));

    [Then("the child's note id is its pid")]
    public async Task ThenOwnPidGroup() => Assert.Equal(child!.Pid, await NoteIdAsync(child.Pid));

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenCallFails(string message) => Assert.Equal(message, failure);

    [Then("the child's noteid reads the process's note id, right-aligned in 11 places and a space")]
    public async Task ThenNoteIdFormat()
        => Assert.Equal((await NoteIdAsync(Init.Pid)).ToString(CultureInfo.InvariantCulture).PadLeft(11) + " ", await ReadAsync(Init, $"/proc/{child!.Pid}/noteid"));

    [Then(@"^reading the child's args gives ""(.*)""$")]
    public async Task ThenArgs(string args) => Assert.Equal(args, await ReadAsync(Init, $"/proc/{child!.Pid}/args"));

    public async ValueTask DisposeAsync()
    {
        holding.TrySetResult(true);
        foreach (Agent agent in agents)
        {
            agent.Stop();
        }

        foreach (int fd in pipes)
        {
            try
            {
                await Init.CloseAsync(fd);
            }
            catch (SyscallException)
            {
                // Already closed by the scenario.
            }
        }
    }

    private static Task Bounded(Task task) => task.WaitAsync(KernelDriver.Bound);

    private static Task<T> Bounded<T>(Task<T> task) => task.WaitAsync(KernelDriver.Bound);

    private static Task<T> Bounded<T>(ValueTask<T> task) => task.AsTask().WaitAsync(KernelDriver.Bound);

    private static async Task<string> ReadAsync(Process process, string path)
    {
        int fd = await process.OpenAsync(path, NinePConstants.OREAD);
        try
        {
            return Encoding.UTF8.GetString((await process.ReadAsync(fd, 256)).Span);
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    private static async Task<string?> WriteAsync(Process process, string path, string text)
    {
        try
        {
            int fd = await process.OpenAsync(path, NinePConstants.OWRITE);
            try
            {
                await process.WriteAsync(fd, Encoding.UTF8.GetBytes(text));
            }
            finally
            {
                await process.CloseAsync(fd);
            }

            return null;
        }
        catch (SyscallException error)
        {
            return error.Message;
        }
    }

    private async Task<Agent> StartAsync(Process parent, bool? continues)
    {
        var agent = new Agent(parent, continues);
        agents.Add(agent);
        await agent.StartAsync();
        return agent;
    }

    private async Task BlockedReadingAsync(bool? continues)
    {
        int read = await PipeAsync();
        child = await StartAsync(Init, continues);
        blocked = child.Do(p => p.ReadAsync(read, 1).AsTask());
    }

    private async Task WriterAsync(bool? continues)
    {
        await PipeAsync();
        await Init.CloseAsync(pipes[^2]);
        child = await StartAsync(Init, continues);
    }

    // A pipe whose two ends the process holds; returns the end read from.
    private async Task<int> PipeAsync()
    {
        var (read, write) = await Init.PipeAsync();
        pipes.AddRange([read, write]);
        return read;
    }

    private async Task RecordAsync(Process process, string path, string text) => writes.Add(await WriteAsync(process, path, text));

    private async Task ChildRecordAsync(string path, string text) => writes.Add(await child!.Do(p => WriteAsync(p, path, text)));

    private async Task<string> ReadNoteAsync(long pid) => (await ReadAsync(Init, $"/proc/{pid}/note")).TrimEnd('\0');

    private async Task<long> NoteIdAsync(long pid) => long.Parse((await ReadAsync(Init, $"/proc/{pid}/noteid")).Trim(), CultureInfo.InvariantCulture);

    private async Task<IEnumerable<string>> NamesAsync(string path)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        try
        {
            return (await Init.DirReadAsync(fd)).Select(entry => entry.Name!).Order(StringComparer.Ordinal).ToList();
        }
        finally
        {
            await Init.CloseAsync(fd);
        }
    }

    // A forked process that runs the scenario's calls one at a time, waiting outside any kernel
    // call between them, with a handler that records each note and continues or leaves it to its
    // default, or with none.
    private sealed class Agent(Process parent, bool? continues)
    {
        private readonly Channel<Func<Process, Task>> calls = Channel.CreateUnbounded<Func<Process, Task>>();
        private readonly TaskCompletionSource<Process> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> seen = [];
        private TaskCompletionSource<bool> noted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Process Parent { get; } = parent;

        public long Pid { get; private set; }

        public async Task StartAsync()
        {
            Pid = Parent.Fork(RforkFlags.Proc, RunAsync);
            await started.Task.WaitAsync(KernelDriver.Bound);
        }

        public async Task<T> Do<T>(Func<Process, Task<T>> call)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await calls.Writer.WriteAsync(async process =>
            {
                try
                {
                    result.SetResult(await call(process));
                }
                catch (ExitException)
                {
                    result.SetCanceled();
                    throw;
                }
                catch (Exception error)
                {
                    result.SetException(error);
                }
            });
            return await result.Task;
        }

        public Task Do(Func<Process, Task> call) => Do(async process =>
        {
            await call(process);
            return true;
        });

        public async Task SawAsync(string note)
        {
            using var bound = new CancellationTokenSource(KernelDriver.Bound);
            while (true)
            {
                Task wait;
                lock (seen)
                {
                    if (seen.Contains(note))
                    {
                        return;
                    }

                    wait = noted.Task;
                }

                await wait.WaitAsync(bound.Token);
            }
        }

        public void Stop() => calls.Writer.TryComplete();

        private async Task RunAsync(Process process)
        {
            if (continues is { } carryOn)
            {
                process.Notify(note =>
                {
                    lock (seen)
                    {
                        seen.Add(note);
                        noted.TrySetResult(true);
                        noted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    return carryOn;
                });
            }

            started.SetResult(process);
            await foreach (Func<Process, Task> call in calls.Reader.ReadAllAsync())
            {
                await call(process);
            }
        }
    }
}
