using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

// How notes reach a handler and how fork, rfork and exec carry a process's handler and note group.
public sealed class NoteTests : IAsyncDisposable
{
    private readonly List<Agent> agents = [];
    private readonly TaskCompletionSource<bool> holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FogKernel kernel = null!;
    private Process init = null!;

    [Fact]
    public async Task ANoteInterruptsAMountWhoseServerDoesNotAnswerItsVersion()
    {
        await BootAsync();
        init.Notify(note => note == "interrupt");
        (int client, int server) = await init.PipeAsync();
        Task mounted = init.MountAsync(client, -1, "/n", NinePSharp.Namespaces.MountFlags.Replace, string.Empty).AsTask();
        byte[] tversion = (await init.ReadAsync(server, 8192).AsTask().WaitAsync(KernelDriver.Bound)).ToArray();
        Assert.Equal((byte)NinePSharp.Constants.MessageTypes.Tversion, tversion[4]);
        Assert.True(init.PostNote("interrupt"));
        Assert.Equal("interrupted", (await Assert.ThrowsAsync<SyscallException>(() => mounted.WaitAsync(KernelDriver.Bound))).Message);
    }

    [Fact]
    public async Task ANoteInterruptsAMountWaitingForAnotherToBeVersioned()
    {
        await BootAsync();
        init.Notify(note => note == "interrupt");
        (int first, int firstServer) = await init.PipeAsync();
        (int second, int secondServer) = await init.PipeAsync();
        Task versioning = init.MountAsync(first, -1, "/n", NinePSharp.Namespaces.MountFlags.Replace, string.Empty).AsTask();
        Assert.Equal((byte)NinePSharp.Constants.MessageTypes.Tversion, (await init.ReadAsync(firstServer, 8192).AsTask().WaitAsync(KernelDriver.Bound)).Span[4]);

        // The second waits its turn behind the first, sending nothing until a note ends its wait.
        var waiting = new TaskCompletionSource<Process>();
        var failed = new TaskCompletionSource<string>();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async c =>
        {
            c.Notify(note => note == "interrupt");
            waiting.SetResult(c);
            try
            {
                await c.MountAsync(second, -1, "/n", NinePSharp.Namespaces.MountFlags.Replace, string.Empty);
                failed.SetResult(string.Empty);
            }
            catch (SyscallException error)
            {
                failed.SetResult(error.Message);
            }
        });
        Process child = await waiting.Task.WaitAsync(KernelDriver.Bound);
        await Task.Delay(50);
        Assert.True(child.PostNote("interrupt"));
        Assert.Equal("interrupted", await failed.Task.WaitAsync(KernelDriver.Bound));
        Assert.True(init.PostNote("interrupt"));
        await Assert.ThrowsAsync<SyscallException>(() => versioning.WaitAsync(KernelDriver.Bound));
        await init.CloseAsync(second);
        Assert.Equal(0, (await init.ReadAsync(secondServer, 8192).AsTask().WaitAsync(KernelDriver.Bound)).Length);
    }

    [Fact]
    public async Task AForkedChildKeepsItsParentsHandler()
    {
        await BootAsync();
        init.Notify(_ => true);
        Agent child = await StartAsync(null);
        Assert.True(child.Process.PostNote("hangup"));

        Assert.True(await child.Do(p => p.OpenAsync("/dev/null", NinePConstants.OREAD).AsTask()) >= 0);
    }

    [Fact]
    public async Task ExecLeavesNotesToTheirDefault()
    {
        await BootAsync();
        Agent child = await StartAsync(true);
        Task exec = child.Do(p => p.ExecAsync("/bin/hold", ["hold"]));
        await held.Task.WaitAsync(KernelDriver.Bound);
        Assert.True(child.Process.PostNote("hangup"));
        holding.SetResult(true);

        Assert.EndsWith(": hangup", (await init.WaitAsync().WaitAsync(KernelDriver.Bound)).Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exec);
    }

    [Fact]
    public async Task ForkWithANewNoteGroupGivesTheChildItsPidAsItsGroup()
    {
        await BootAsync();
        Agent child = await StartAsync(true, RforkFlags.Proc | RforkFlags.Noteg);

        Assert.Equal(child.Pid, child.Process.NoteId);
        Assert.NotEqual(init.NoteId, child.Process.NoteId);
    }

    [Fact]
    public async Task RforkKeepsTheNoteGroupUnlessAskedForANewOne()
    {
        await BootAsync();
        Agent child = await StartAsync(true);
        await child.Do(p => p.RforkAsync(RforkFlags.Fdg).AsTask());

        Assert.Equal(init.NoteId, child.Process.NoteId);
    }

    [Fact]
    public async Task ASecondNewNoteGroupKeepsTheNumberOnlyTheProcessHolds()
    {
        await BootAsync();
        Agent child = await StartAsync(true);
        await child.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());
        long group = child.Process.NoteId;
        await child.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());

        Assert.NotEqual(init.NoteId, group);
        Assert.Equal(group, child.Process.NoteId);
    }

    [Fact]
    public async Task ANewNoteGroupLeavesAGroupNamedForTheProcessesPid()
    {
        await BootAsync();
        Agent child = await StartAsync(true, RforkFlags.Proc | RforkFlags.Noteg);
        await child.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());

        Assert.NotEqual(child.Pid, child.Process.NoteId);
    }

    [Fact]
    public async Task ANewNoteGroupLeavesAGroupAnotherProcessShares()
    {
        await BootAsync();
        Agent child = await StartAsync(true);
        await child.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());
        Agent grandchild = await StartAsync(true, parent: child.Process);
        await grandchild.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());

        Assert.NotEqual(child.Process.NoteId, grandchild.Process.NoteId);
    }

    // The group named for an exited process's pid is still held by its children, as their parent's
    // pid, and by every process in it.
    [Fact]
    public async Task ANewNoteGroupLeavesAGroupItsParentsPidStillNames()
    {
        await BootAsync();
        Agent child = await StartAsync(true, RforkFlags.Proc | RforkFlags.Noteg);
        long group = child.Pid;
        Agent grandchild = await StartAsync(true, parent: child.Process);
        Agent joined = await StartAsync(true);
        kernel.ChangeNoteId(null, joined.Process, group);
        kernel.ChangeNoteId(null, child.Process, init.NoteId);
        child.Stop();
        await init.WaitAsync().WaitAsync(KernelDriver.Bound);

        await joined.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());
        await grandchild.Do(p => p.RforkAsync(RforkFlags.Noteg).AsTask());

        Assert.NotEqual(group, joined.Process.NoteId);
        Assert.NotEqual(group, grandchild.Process.NoteId);
    }

    [Fact]
    public async Task EachNoteIsHandledOnceAndNotWhileAnotherIsBeingHandled()
    {
        await BootAsync();
        var seen = new List<string>();
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        init.Fork(RforkFlags.Proc, async p =>
        {
            p.Notify(note =>
            {
                seen.Add($"begin {note}");
                p.Fork(RforkFlags.Proc, _ => Task.CompletedTask);
                seen.Add($"end {note}");
                return true;
            });
            started.SetResult(p);
            await finish.Task;
            for (int call = 0; call < 3; call++)
            {
                await p.OpenAsync("/dev/null", NinePConstants.OREAD);
            }

            calls.SetResult(true);
        });
        Process child = await started.Task.WaitAsync(KernelDriver.Bound);
        Assert.True(child.PostNote("a"));
        Assert.True(child.PostNote("b"));
        finish.SetResult(true);
        await calls.Task.WaitAsync(KernelDriver.Bound);

        Assert.Equal(["begin a", "end a", "begin b", "end b"], seen);
    }

    [Fact]
    public async Task ANoteWaitingAsACallBeginsIsHandledBeforeTheCallDoesItsWork()
    {
        await BootAsync();
        bool? existed = null;
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        init.Fork(RforkFlags.Proc, async p =>
        {
            p.Notify(_ =>
            {
                existed = Exists(p, "/tmp/made");
                return true;
            });
            started.SetResult(p);
            await go.Task;
            await p.CloseAsync(await p.CreateAsync("/tmp/made", NinePConstants.OWRITE, 0b110_110_110));
            done.SetResult();
        });
        Process child = await started.Task.WaitAsync(KernelDriver.Bound);
        Assert.True(child.PostNote("alarm"));
        go.SetResult();
        await done.Task.WaitAsync(KernelDriver.Bound);

        Assert.False(existed);
    }

    [Fact]
    public async Task AfterAHandledNoteACallBlocksAgainUntilItIsDone()
    {
        await BootAsync();
        var (read, write) = await init.PipeAsync();
        Agent child = await StartAsync(true);
        Task<ReadOnlyMemory<byte>> first = await child.BeginAsync(p => p.ReadAsync(read, 1).AsTask());
        Assert.True(child.Process.PostNote("hangup"));
        Assert.Equal(Errors.Interrupted, (await Assert.ThrowsAsync<SyscallException>(() => first.WaitAsync(KernelDriver.Bound))).Message);

        Task<ReadOnlyMemory<byte>> second = await child.BeginAsync(p => p.ReadAsync(read, 1).AsTask());
        await init.WriteAsync(write, "x"u8.ToArray());

        Assert.Equal("x", Encoding.ASCII.GetString((await second.WaitAsync(KernelDriver.Bound)).Span));
    }

    [Fact]
    public async Task AHostCallGivesWhatTheHostGivesAndANoteInterruptsIt()
    {
        await BootAsync();
        Agent child = await StartAsync(true);
        Assert.Same(kernel.Host, await child.Do(p => p.HostAsync(host => Task.FromResult(host))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.Do(p => p.HostAsync<int>(_ => Task.FromException<int>(new InvalidOperationException()))));

        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> call = await child.BeginAsync(p => p.HostAsync(_ => never.Task));
        Assert.True(child.Process.PostNote("hangup"));
        Assert.Equal(Errors.Interrupted, (await Assert.ThrowsAsync<SyscallException>(() => call.WaitAsync(KernelDriver.Bound))).Message);
    }

    [Fact]
    public async Task ANoteToAProcessWithNoHandlerEndsItInAHostCall()
    {
        await BootAsync();
        Agent child = await StartAsync(null);
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = await child.BeginAsync(p => p.HostAsync(_ => never.Task));
        Assert.True(child.Process.PostNote("hangup"));

        Assert.EndsWith(": hangup", (await init.WaitAsync().WaitAsync(KernelDriver.Bound)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExitedProcessTakesNoNoteAndKeepsNoneThatWaited()
    {
        await BootAsync();
        Agent child = await StartAsync(true);
        Assert.True(child.Process.PostNote("hangup"));
        child.Stop();
        await init.WaitAsync().WaitAsync(KernelDriver.Bound);

        Assert.Null(child.Process.TakeNote());
        Assert.False(child.Process.PostNote("alarm"));
    }

    [Fact]
    public async Task APwriteBlockedOnAFullPipeIsInterruptedByANoteAndCancelledByItsCaller()
    {
        await BootAsync();
        var (_, write) = await init.PipeAsync();
        byte[] full = new byte[PipeQueue.MaxLimit + 1];
        Agent child = await StartAsync(true);
        Task<int> blocked = await child.BeginAsync(p => p.PWriteAsync(write, full, -1).AsTask());
        Assert.True(child.Process.PostNote("hangup"));
        Assert.Equal(Errors.Interrupted, (await Assert.ThrowsAsync<SyscallException>(() => blocked.WaitAsync(KernelDriver.Bound))).Message);

        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => init.PWriteAsync(write, full, -1, cancel.Token).AsTask().WaitAsync(KernelDriver.Bound));
    }

    [Theory]
    [InlineData(122, "", 122)]
    [InlineData(121, "é", 121)]
    [InlineData(121, "€", 120)]
    public async Task ExecKeepsAtMost128BytesOfArgumentsCutAsSysexecCutsThem(int length, string tail, int kept)
    {
        await BootAsync();
        Agent child = await StartAsync(true);
        _ = child.Do(p => p.ExecAsync("/bin/hold", ["hold", new string('a', length) + tail]));
        await held.Task.WaitAsync(KernelDriver.Bound);

        Assert.Equal("hold " + new string('a', kept), child.Process.Args);
    }

    public ValueTask DisposeAsync()
    {
        holding.TrySetResult(true);
        foreach (Agent agent in agents)
        {
            agent.Stop();
        }

        return ValueTask.CompletedTask;
    }

    private static bool Exists(Process process, string path)
    {
        try
        {
            process.StatAsync(path).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (SyscallException)
        {
            return false;
        }
    }

    private async Task BootAsync()
    {
        var programs = new Dictionary<string, ProgramMain>
        {
            ["hold"] = async (p, _) =>
            {
                held.TrySetResult(true);
                await holding.Task;
                await p.OpenAsync("/dev/null", NinePConstants.OREAD);
            },
        };
        kernel = FogKernel.InMemory(programs, "eve");
        init = await kernel.BootAsync();
    }

    private async Task<Agent> StartAsync(bool? continues, RforkFlags flags = RforkFlags.Proc, Process? parent = null)
    {
        var agent = new Agent(parent ?? init, continues, flags);
        agents.Add(agent);
        await agent.StartAsync();
        return agent;
    }
}
