using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class JobTreeTests
{
    private static readonly FogPrincipal Principal = new("client", new string('1', 64), 1, new string('2', 64));
    private static readonly FogMathJob Spec = new("solve", "x", 5000, 15000, 268435456, 65536);

    [Fact]
    public async Task ReconnectAndStartReplayExecuteOnceAndRetainImmutableResult()
    {
        var runner = new ControlledRunner();
        await using var tree = Create(runner);
        string id = Clone(tree);
        Stage(tree, id);
        await Control(tree, id, "start\n");
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tree.CloseSession("s");
        await Control(tree, id, "start\n");
        Assert.Throws<FogException>(() => Open(tree, id, "result", NinePConstants.OREAD));
        using var before = Open(tree, id, "status", NinePConstants.OREAD);
        runner.Done.SetResult(new("answer"u8.ToArray(), null, 42));
        await Terminal(tree, id);
        using var result = Open(tree, id, "result", NinePConstants.OREAD);
        Assert.Equal("answer", Encoding.UTF8.GetString(result.Snapshot!));
        Assert.Equal("running", FogMathJob.StatusSchema.Parse(before.Snapshot!, 2048, 1)[0]["state"]);
        await Control(tree, id, "start\n");
        Assert.Equal(1, runner.Calls);
        FogFileNode stale = Node(tree, id, "result");
        await Control(tree, id, "release\n");
        Assert.Equal("job-expired", Assert.Throws<FogException>(() => tree.Check(Principal, stale)).Code);
    }

    [Fact]
    public async Task AbortedReplacementPreservesPreviouslySealedInput()
    {
        var runner = new ControlledRunner();
        await using var tree = Create(runner);
        string id = Clone(tree);
        Stage(tree, id);
        using (var replacement = Open(tree, id, "input", NinePConstants.OWRITE))
        {
            await replacement.Write!(0, "bad"u8.ToArray(), default);
            tree.CloseSession("s");
            replacement.Clunk();
        }
        await Control(tree, id, "start\n");
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("x^2-4", Encoding.UTF8.GetString(runner.Input!));
        runner.Done.SetResult(new([], null, 0));
        await Terminal(tree, id);
    }

    [Fact]
    public async Task OneWriterContiguousWritesAndSealAreRequired()
    {
        await using var tree = Create(new ControlledRunner());
        string id = Clone(tree);
        using var upload = Open(tree, id, "input", NinePConstants.OWRITE);
        Assert.Throws<FogException>(() => Open(tree, id, "spec", NinePConstants.OWRITE));
        await Assert.ThrowsAsync<FogException>(() => upload.Write!(1, new byte[1], default));
        await Assert.ThrowsAsync<FogException>(() => upload.Write!(0, new byte[1048577], default));
        await Assert.ThrowsAsync<FogException>(() => Control(tree, id, "start\n"));
        await upload.Write!(0, "1"u8.ToArray(), default);
        upload.Clunk();
        await Assert.ThrowsAsync<FogException>(() => upload.Write!(1, "1"u8.ToArray(), default));
        await Assert.ThrowsAsync<FogException>(() => Control(tree, id, "start\n"));
    }

    [Fact]
    public async Task CancelWaitsForCleanupAndDiscardsCandidateResult()
    {
        var runner = new ControlledRunner();
        await using var tree = Create(runner);
        string id = Clone(tree);
        Stage(tree, id);
        await Control(tree, id, "start\n");
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Control(tree, id, "cancel\n");
        await Control(tree, id, "cancel\n");
        Assert.Equal("running", Status(tree, id)["state"]);
        await Assert.ThrowsAsync<FogException>(() => Control(tree, id, "release\n"));
        runner.Done.SetResult(new("hidden"u8.ToArray(), null, 10));
        await Terminal(tree, id);
        Assert.Equal("cancelled", Status(tree, id)["state"]);
        Assert.Equal("0", Status(tree, id)["result_bytes"]);
        Assert.True(runner.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task OwnerAndPolicyFenceEveryFile()
    {
        bool allowed = true;
        await using var tree = new FogJobFileTree(new ControlledRunner(), _ => allowed);
        string id = Clone(tree);
        FogFileNode input = Node(tree, id, "input");
        Assert.Throws<FogException>(() => tree.Check(Principal with { Node = "other" }, input));
        allowed = false;
        Assert.Throws<FogException>(() => tree.Open(Principal, "s", input, NinePConstants.OWRITE, 65536));
    }

    [Fact]
    public async Task CapacityRetentionAndModeAreBounded()
    {
        var clock = new ControlFixture.ManualTime();
        await using var tree = new FogJobFileTree(new ControlledRunner(), _ => true, 1, TimeSpan.FromSeconds(1), clock);
        string id = Clone(tree);
        Assert.Throws<FogException>(() => Clone(tree));
        Assert.Throws<FogException>(() => Open(tree, id, "status", NinePConstants.OWRITE));
        Assert.Throws<FogException>(() => tree.Open(Principal, "s", Node(tree, id, "status"), NinePConstants.OREAD, 1));
        await Assert.ThrowsAsync<FogException>(() => Control(tree, id, "start"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(id, Clone(tree));
        Assert.Throws<FogException>(() => Node(tree, id, "status"));
    }

    [Theory]
    [InlineData("runtime=math", "runtime=lisp")]
    [InlineData("p_cpu_ms=5000", "p_cpu_ms=0")]
    [InlineData("p_cpu_ms=5000", "p_cpu_ms=05000")]
    [InlineData("deadline_ms=15000", "deadline_ms=60001")]
    [InlineData("memory_bytes=268435456", "memory_bytes=1024")]
    [InlineData("p_variable=x", "p_variable=x/2")]
    [InlineData("p_operation=solve", "p_operation=execute")]
    [InlineData("source=/compute/42/input", "source=/etc/passwd")]
    public void ClosedSpecRejectsInvalidContracts(string from, string to)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Spec.Serialize("42")).Replace(from, to, StringComparison.Ordinal));
        Assert.Throws<FogException>(() => FogMathJob.Parse("42", bytes));
    }

    internal static FogJobFileTree Create(IFogMathRunner runner) => new(runner, owner => owner == Principal.Owner);
    private static FogFileNode Node(FogJobFileTree tree, string id, string file) =>
        tree.Walk(Principal, tree.Walk(Principal, tree.Walk(Principal, tree.Root, "compute"), id), file);
    private static FogOpenFile Open(FogJobFileTree tree, string id, string file, byte mode) => tree.Open(Principal, "s", Node(tree, id, file), mode, 65536);
    private static string Clone(FogJobFileTree tree)
    {
        var file = tree.Walk(Principal, tree.Walk(Principal, tree.Root, "compute"), "clone");
        using var opened = tree.Open(Principal, "s", file, NinePConstants.OREAD, 1024);
        return Encoding.ASCII.GetString(opened.Snapshot!).TrimEnd('\n');
    }
    private static void Stage(FogJobFileTree tree, string id)
    {
        foreach (var file in new Dictionary<string, byte[]> { ["spec"] = Spec.Serialize(id), ["input"] = "x^2-4"u8.ToArray() })
        {
            using var upload = Open(tree, id, file.Key, NinePConstants.OWRITE);
            upload.Write!(0, file.Value, default).GetAwaiter().GetResult();
            upload.Clunk();
        }
    }
    private static async Task Control(FogJobFileTree tree, string id, string command)
    {
        using var ctl = Open(tree, id, "ctl", NinePConstants.OWRITE);
        await ctl.Write!(123, Encoding.ASCII.GetBytes(command), default);
        ctl.Clunk();
    }
    private static IReadOnlyDictionary<string, string?> Status(FogJobFileTree tree, string id)
    {
        using var file = Open(tree, id, "status", NinePConstants.OREAD);
        return FogMathJob.StatusSchema.Parse(file.Snapshot!, 2048, 1)[0];
    }
    private static async Task Terminal(FogJobFileTree tree, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Status(tree, id)["state"] is "queued" or "running") await Task.Delay(1, timeout.Token);
    }
    internal sealed class ControlledRunner : IFogMathRunner
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<FogMathOutcome> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal byte[]? Input;
        internal CancellationToken Cancellation;
        public Task<FogMathOutcome> RunAsync(FogMathJob job, byte[] input, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            Input = input;
            Cancellation = cancellation;
            Started.SetResult();
            return Done.Task;
        }
    }
}
