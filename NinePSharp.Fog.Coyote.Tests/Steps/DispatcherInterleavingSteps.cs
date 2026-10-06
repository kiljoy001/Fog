using Microsoft.Coyote;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Extensions.Logging;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Fog.Server.Tests;
using NinePSharp.Messages;
using NinePSharp.Parser;
using Reqnroll;
using Reqnroll.UnitTestProvider;
using Xunit;

namespace NinePSharp.Fog.Coyote.Tests.Steps;

[Binding]
public sealed class DispatcherInterleavingSteps(IUnitTestRuntimeProvider runtime, IReqnrollOutputHelper output) : IDisposable
{
    private const string Session = "node";
    private const uint Iterations = 500;
    private ControlFixture? fixture;
    private TimeSpan drain;
    private WriteBehaviour behaviour;
    private Func<Iteration, Task<Dictionary<string, object>>>? race;

    private enum WriteBehaviour
    {
        Released,
        ReleasedOrCancelled,
        Never,
    }

    public void Dispose() => fixture?.Dispose();

    [Given(@"^a node session with ""(.*)"" open for writing on a dispatcher whose drain limit is (\d+) minutes$")]
    public void GivenSession(string name, int minutes)
    {
        if (!RewritingEngine.IsAssemblyRewritten(typeof(FogNinePDispatcher).Assembly))
        {
            runtime.TestIgnore("the Fog assemblies were not rewritten with coyote rewrite");
        }

        fixture = new ControlFixture();
        drain = TimeSpan.FromMinutes(minutes);
    }

    [Given(@"^the dispatcher's drain limit is (\d+) milliseconds$")]
    public void GivenDrain(int milliseconds) => drain = TimeSpan.FromMilliseconds(milliseconds);

    [Given(@"^a write to ""(.*)"" that finishes when it is released$")]
    public void GivenReleasedWrite(string name) => behaviour = WriteBehaviour.Released;

    [Given(@"^a write to ""(.*)"" that finishes when it is released or cancelled$")]
    public void GivenCooperativeWrite(string name) => behaviour = WriteBehaviour.ReleasedOrCancelled;

    [Given(@"^a write to ""(.*)"" that never finishes$")]
    public void GivenStuckWrite(string name) => behaviour = WriteBehaviour.Never;

    [When("the node flushes the write while another task releases it")]
    public void WhenFlushRacesRelease() => race = async iteration =>
    {
        Task<object> write = iteration.Write();
        Task released = Task.Run(iteration.Release);
        object flush = await Task.Run(() => iteration.Send(NinePMessage.NewMsgTflush(new Tflush(101, 100))));
        object reuse = await iteration.Send(NinePMessage.NewMsgTstat(new Tstat(100, 1)));
        await Task.WhenAll(new Task[] { write, released });
        return new() { ["write"] = await write, ["flush"] = flush, ["reuse"] = reuse };
    };

    [When("the session closes while another task releases the write")]
    public void WhenCloseRacesRelease() => race = async iteration =>
    {
        Task<object> write = iteration.Write();
        Task released = Task.Run(iteration.Release);
        await Task.WhenAll(new Task[] { iteration.Dispatcher.CloseSessionAsync(Session), released });
        return new() { ["write"] = await write };
    };

    [When("the node sends Tversion while another task releases the write")]
    public void WhenVersionRacesRelease() => race = async iteration =>
    {
        Task<object> write = iteration.Write();
        Task released = Task.Run(iteration.Release);
        object version = await Task.Run(() => iteration.Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000"))));
        object reuse = await iteration.Send(NinePMessage.NewMsgTattach(new Tattach(100, 1, NinePConstants.NoFid, "worker", "runtime")));
        await Task.WhenAll(new Task[] { write, released });
        return new() { ["write"] = await write, ["version"] = version, ["reuse"] = reuse };
    };

    [When("the node flushes the write twice while another task releases it")]
    public void WhenTwoFlushesRaceRelease() => race = async iteration =>
    {
        Task<object> write = iteration.Write();
        Task released = Task.Run(iteration.Release);
        object[] flushes = await Task.WhenAll(new[]
        {
            Task.Run(() => iteration.Send(NinePMessage.NewMsgTflush(new Tflush(101, 100)))),
            Task.Run(() => iteration.Send(NinePMessage.NewMsgTflush(new Tflush(102, 100)))),
        });
        object reuse = await iteration.Send(NinePMessage.NewMsgTstat(new Tstat(100, 1)));
        await Task.WhenAll(new Task[] { write, released });
        return new() { ["write"] = await write, ["flush"] = flushes[0], ["flush2"] = flushes[1], ["reuse"] = reuse };
    };

    [When("the node flushes the write while the session closes")]
    public void WhenFlushRacesClose() => race = async iteration =>
    {
        Task<object> write = iteration.Write();
        Task<object> flush = Task.Run(() => iteration.Send(NinePMessage.NewMsgTflush(new Tflush(101, 100))));
        Task closed = Task.Run(() => iteration.Dispatcher.CloseSessionAsync(Session));
        await Task.WhenAll(new Task[] { flush, closed });
        return new() { ["write"] = await write, ["flush"] = await flush };
    };

    [When("the node flushes the write")]
    public void WhenFlush() => race = async iteration =>
    {
        Task<object> write = iteration.Write();
        object flush = await iteration.Send(NinePMessage.NewMsgTflush(new Tflush(101, 100)));
        object reuse = await iteration.Send(NinePMessage.NewMsgTstat(new Tstat(100, 1)));
        return new() { ["write"] = await write, ["flush"] = flush, ["reuse"] = reuse };
    };

    [Then(@"^in every explored schedule the write is answered with Rwrite or Rerror ""interrupted""$")]
    public void ThenWriteAnswered() => Explore((_, replies) =>
        Specification.Assert(
            replies["write"] is Rwrite || replies["write"] is Rerror { Ename: "interrupted" },
            $"the write was answered {Describe(replies["write"])}"));

    [Then(@"^in every explored schedule the write is answered with Rerror ""unknown""$")]
    public void ThenWriteAbandoned() => Explore((_, replies) =>
        Specification.Assert(replies["write"] is Rerror { Ename: "unknown" }, $"the write was answered {Describe(replies["write"])}"));

    [Then("in every explored schedule the flush is answered with Rflush")]
    public void ThenFlushed() => Explore((_, replies) =>
        Specification.Assert(replies["flush"] is Rflush, $"the flush was answered {Describe(replies["flush"])}"));

    [Then(@"^in every explored schedule the flush is answered with Rflush or Rerror ""not-ready""$")]
    public void ThenFlushedOrClosed() => Explore((_, replies) =>
        Specification.Assert(
            replies["flush"] is Rflush || replies["flush"] is Rerror { Ename: "not-ready" },
            $"the flush was answered {Describe(replies["flush"])}"));

    [Then("in every explored schedule each flush is answered with Rflush or finishes without a reply, and one with Rflush")]
    public void ThenEachFlush() => Explore((_, replies) =>
    {
        object[] answers = [replies["flush"], replies["flush2"]];
        foreach (object answer in answers)
        {
            Specification.Assert(answer is Rflush || answer == FogNinePDispatcher.NoReply, $"a flush was answered {Describe(answer)}");
        }

        Specification.Assert(answers.Any(answer => answer is Rflush), "neither flush was answered with Rflush");
    });

    [Then("in every explored schedule the write's tag can be used again the moment the flush is answered")]
    [Then("in every explored schedule the write's tag can be used again the moment the flushes are answered")]
    [Then("in every explored schedule the write's tag can be used again the moment Rversion is answered")]
    public void ThenTagFree() => Explore((_, replies) =>
        Specification.Assert(replies["reuse"] is not Rerror, $"reusing the write's tag was answered {Describe(replies["reuse"])}"));

    [Then("no explored schedule logs an outcome as unknown")]
    public void ThenNothingUnknown() => Explore((iteration, _) =>
        Specification.Assert(iteration.Unknowns() == 0, "an outcome was logged as unknown"));

    [Then("in every explored schedule the write's outcome is logged as unknown exactly once")]
    public void ThenUnknownOnce() => Explore((iteration, _) =>
        Specification.Assert(iteration.Unknowns() == 1, $"the outcome was logged as unknown {iteration.Unknowns()} times"));

    private static string Describe(object reply) =>
        reply is Rerror error ? $"Rerror \"{error.Ename}\"" : reply == FogNinePDispatcher.NoReply ? "nothing" : reply.GetType().Name;

    private void Explore(Action<Iteration, Dictionary<string, object>> rule)
    {
        using var engine = TestingEngine.Create(
            Configuration.Create().WithTestingIterations(Iterations).WithMaxSchedulingSteps(5000),
            async () =>
            {
                Iteration iteration = await Iteration.OpenAsync(fixture!, drain, behaviour);
                rule(iteration, await race!(iteration));
            });
        engine.Run();
        TestReport report = engine.TestReport;
        Assert.True(report.NumOfFoundBugs == 0, string.Join(Environment.NewLine, report.BugReports) + Environment.NewLine + engine.ReproducibleTrace);
        if (report.UncontrolledInvocations.Count != 0)
        {
            output.WriteLine("Coyote did not control: " + string.Join(", ", report.UncontrolledInvocations.Order(StringComparer.Ordinal)));
        }

        Assert.Equal(Iterations, (uint)report.NumOfExploredFairPaths + (uint)report.NumOfExploredUnfairPaths);
    }

    private sealed class Iteration
    {
        private readonly TaskCompletionSource<uint> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ControlFixture fixture;

        private Iteration(ControlFixture fixture, TimeSpan drain, WriteBehaviour behaviour)
        {
            this.fixture = fixture;
            var tree = new ProbeTree
            {
                OnOpen = () => new FogOpenFile(write: (_, _, cancellation) =>
                {
                    if (behaviour == WriteBehaviour.ReleasedOrCancelled)
                    {
                        cancellation.Register(() => release.TrySetCanceled(cancellation));
                    }

                    return behaviour == WriteBehaviour.Never ? new TaskCompletionSource<uint>().Task : release.Task;
                }),
            };
            Dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits with { Drain = drain }, fixture.Time, Logger);
        }

        internal RecordingLogger Logger { get; } = new();

        internal FogNinePDispatcher Dispatcher { get; }

        internal static async Task<Iteration> OpenAsync(ControlFixture fixture, TimeSpan drain, WriteBehaviour behaviour)
        {
            var iteration = new Iteration(fixture, drain, behaviour);
            Assert.IsType<Rversion>(await iteration.Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000"))));
            Assert.IsType<Rattach>(await iteration.Send(NinePMessage.NewMsgTattach(new Tattach(1, 1, NinePConstants.NoFid, "worker", "runtime"))));
            Assert.IsType<Rwalk>(await iteration.Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, ["file"]))));
            Assert.IsType<Ropen>(await iteration.Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE))));
            return iteration;
        }

        internal Task<object> Send(NinePMessage message) => Dispatcher.DispatchAsync(Session, message, NinePDialect.NineP2000, fixture.NodeCertificate);

        internal Task<object> Write() => Send(NinePMessage.NewMsgTwrite(new Twrite(100, 2, 0, new byte[] { 1 })));

        internal void Release() => release.TrySetResult(1);

        internal int Unknowns() => Logger.At(LogLevel.Warning).Count(message => message.Contains("outcome is unknown", StringComparison.Ordinal));
    }
}
