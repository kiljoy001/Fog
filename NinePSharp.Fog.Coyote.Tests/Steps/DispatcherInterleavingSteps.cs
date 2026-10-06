using Microsoft.Coyote;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Extensions.Logging;
using NinePSharp.Constants;
using NinePSharp.Fog.Coyote.Tests.Support;
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

    [When(@"^the node walks ""(.*)"" to a new fid, opens it and stats it without waiting for the answers$")]
    public void WhenPipelined(string name) => race = async iteration =>
    {
        Task<object> walk = iteration.Send(NinePMessage.NewMsgTwalk(new Twalk(10, 1, 3, [name])));
        Task<object> open = iteration.Send(NinePMessage.NewMsgTopen(new Topen(11, 3, NinePConstants.OREAD)));
        Task<object> stat = iteration.Send(NinePMessage.NewMsgTstat(new Tstat(12, 3)));
        return new() { ["walk"] = await walk, ["open"] = await open, ["stat"] = await stat };
    };

    [Then("in every explored schedule the walk, open and stat are each answered without an error")]
    public void ThenInOrder() => Explore((_, replies) =>
    {
        foreach (string request in new[] { "walk", "open", "stat" })
        {
            Specification.Assert(replies[request] is not Rerror, $"the {request} was answered {Describe(replies[request])}");
        }
    });

    [Then("Coyote reports no wait it did not control")]
    public void ThenAllControlled()
    {
        TestReport report = Run((_, _) => { });
        Assert.Empty(report.UncontrolledInvocations);
    }

    [When("the node writes on both fids and flushes the first while other tasks release both writes")]
    public void WhenFlushOneOfTwo() => race = async iteration =>
    {
        await iteration.OpenAsync(Session, 3);
        Task<object> write = iteration.Write();
        Task<object> second = iteration.Write(Session, 102, 3, 2);
        Task released = Task.Run(iteration.Release);
        Task releasedSecond = Task.Run(() => iteration.Release(2));
        object flush = await Task.Run(() => iteration.Send(NinePMessage.NewMsgTflush(new Tflush(101, 100))));
        object[] writes = await Task.WhenAll(write, second);
        await Task.WhenAll(released, releasedSecond);
        object reuse = await iteration.Send(NinePMessage.NewMsgTstat(new Tstat(100, 1)));
        object reuseSecond = await iteration.Send(NinePMessage.NewMsgTstat(new Tstat(102, 1)));
        return new() { ["write"] = writes[0], ["second"] = writes[1], ["flush"] = flush, ["reuse"] = reuse, ["reuse2"] = reuseSecond };
    };

    [When("the node clunks the write's fid while another task releases the write")]
    public void WhenClunkRacesRelease() => race = async iteration =>
    {
        Task<object> write = iteration.Write();
        Task released = Task.Run(iteration.Release);
        object clunk = await Task.Run(() => iteration.Send(NinePMessage.NewMsgTclunk(new Tclunk(101, 2))));
        object written = await write;
        await released;
        object again = await iteration.Send(NinePMessage.NewMsgTclunk(new Tclunk(102, 2)));
        return new() { ["write"] = written, ["clunk"] = clunk, ["again"] = again };
    };

    [When("the node flushes the write and sends Tversion at once while another task releases the write")]
    public void WhenFlushRacesVersion() => race = async iteration =>
    {
        Task<object> write = iteration.Write();
        Task released = Task.Run(iteration.Release);
        Task<object> flush = Task.Run(() => iteration.Send(NinePMessage.NewMsgTflush(new Tflush(101, 100))));
        Task<object> version = Task.Run(() => iteration.Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000"))));
        await Task.WhenAll(new Task[] { flush, version, released });
        return new() { ["write"] = await write, ["flush"] = await flush, ["version"] = await version };
    };

    [When("a second session writes too, and both sessions close at once while another task releases the writes")]
    public void WhenTwoSessionsClose() => race = async iteration =>
    {
        await iteration.OpenSessionAsync("node2");
        Task<object> write = iteration.Write();
        Task<object> second = iteration.Write("node2", 100, 2, 2);
        Task released = Task.Run(() =>
        {
            iteration.Release();
            iteration.Release(2);
        });
        await Task.WhenAll(new Task[]
        {
            Task.Run(() => iteration.Dispatcher.CloseSessionAsync(Session)),
            Task.Run(() => iteration.Dispatcher.CloseSessionAsync("node2")),
            released,
        });
        return new() { ["write"] = await write, ["second"] = await second };
    };

    [Then("in every explored schedule the second write is answered with Rwrite")]
    public void ThenSecondWritten() => Explore((_, replies) =>
        Specification.Assert(replies["second"] is Rwrite, $"the second write was answered {Describe(replies["second"])}"));

    [Then("in every explored schedule both writes' tags can be used again once all are answered")]
    public void ThenBothTagsFree() => Explore((_, replies) =>
    {
        foreach (string reuse in new[] { "reuse", "reuse2" })
        {
            Specification.Assert(replies[reuse] is not Rerror, $"reusing a write's tag was answered {Describe(replies[reuse])}");
        }
    });

    [Then("in every explored schedule the write is answered with Rwrite")]
    public void ThenWritten() => Explore((_, replies) =>
        Specification.Assert(replies["write"] is Rwrite, $"the write was answered {Describe(replies["write"])}"));

    [Then(@"^in every explored schedule one clunk is answered with Rclunk and the other with Rerror ""busy"" before it or ""invalid-request"" after it$")]
    public void ThenClunkedOnce() => Explore((_, replies) =>
    {
        bool first = replies["clunk"] is Rclunk && replies["again"] is Rerror { Ename: "invalid-request" };
        bool second = replies["clunk"] is Rerror { Ename: "busy" } && replies["again"] is Rclunk;
        Specification.Assert(first || second, $"the clunks were answered {Describe(replies["clunk"])} and {Describe(replies["again"])}");
    });

    [Then("in every explored schedule Tversion is answered with Rversion")]
    public void ThenVersioned() => Explore((_, replies) =>
        Specification.Assert(replies["version"] is Rversion, $"Tversion was answered {Describe(replies["version"])}"));

    [Then(@"^in every explored schedule the second session's write is answered with Rwrite or Rerror ""interrupted""$")]
    public void ThenSecondSessionWrite() => Explore((_, replies) =>
        Specification.Assert(
            replies["second"] is Rwrite || replies["second"] is Rerror { Ename: "interrupted" },
            $"the second session's write was answered {Describe(replies["second"])}"));

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

    private void Explore(Action<Iteration, Dictionary<string, object>> rule) => Run(rule);

    private TestReport Run(Action<Iteration, Dictionary<string, object>> rule) => Explorer.Explore(
        async () =>
        {
            Iteration iteration = await Iteration.OpenAsync(fixture!, drain, behaviour);
            rule(iteration, await race!(iteration));
        },
        output);

    private sealed class Iteration
    {
        private readonly TaskCompletionSource<uint>[] releases = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        private readonly ControlFixture fixture;

        private Iteration(ControlFixture fixture, TimeSpan drain, WriteBehaviour behaviour)
        {
            this.fixture = fixture;
            var tree = new ProbeTree
            {
                // A write of 1 waits for the first release and a write of 2 for the second.
                OnOpen = () => new FogOpenFile(write: (_, data, cancellation) =>
                {
                    TaskCompletionSource<uint> release = releases[data.Span[0] - 1];
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
            await iteration.OpenSessionAsync(Session);
            return iteration;
        }

        // Version, attach fid 1 and open "file" for writing as fid 2.
        internal async Task OpenSessionAsync(string session)
        {
            Assert.IsType<Rversion>(await Send(session, NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000"))));
            Assert.IsType<Rattach>(await Send(session, NinePMessage.NewMsgTattach(new Tattach(1, 1, NinePConstants.NoFid, "worker", "runtime"))));
            await OpenAsync(session, 2);
        }

        internal async Task OpenAsync(string session, uint fid)
        {
            Assert.IsType<Rwalk>(await Send(session, NinePMessage.NewMsgTwalk(new Twalk(2, 1, fid, ["file"]))));
            Assert.IsType<Ropen>(await Send(session, NinePMessage.NewMsgTopen(new Topen(3, fid, NinePConstants.OWRITE))));
        }

        internal Task<object> Send(NinePMessage message) => Send(Session, message);

        internal Task<object> Send(string session, NinePMessage message) => Dispatcher.DispatchAsync(session, message, NinePDialect.NineP2000, fixture.NodeCertificate);

        internal Task<object> Write() => Write(Session, 100, 2, 1);

        // A write on the fid with the tag, finishing at the release of that number.
        internal Task<object> Write(string session, ushort tag, uint fid, byte release)
            => Send(session, NinePMessage.NewMsgTwrite(new Twrite(tag, fid, 0, new byte[] { release })));

        internal void Release() => Release(1);

        internal void Release(byte release) => releases[release - 1].TrySetResult(1);

        internal int Unknowns() => Logger.At(LogLevel.Warning).Count(message => message.Contains("outcome is unknown", StringComparison.Ordinal));
    }
}
