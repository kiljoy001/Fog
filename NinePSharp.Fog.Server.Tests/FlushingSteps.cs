using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Parser;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

[Binding]
[Scope(Feature = "Every Tflush is answered with Rflush, as flush(5) requires")]
public sealed class FlushingSteps : IDisposable
{
    private const string Session = "node";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly List<TaskCompletionSource<uint>> releases = new();
    private readonly List<Task<object>> writes = new();
    private readonly List<Task<object>> flushes = new();
    private ControlFixture? fixture;
    private FogNinePDispatcher? dispatcher;
    private TimeSpan drain;
    private int requests;
    private bool honoursCancellation;

    public void Dispose()
    {
        foreach (var release in releases)
        {
            release.TrySetResult(1);
        }

        fixture?.Dispose();
    }

    [Given(@"^a control export with a drain limit of (\d+) seconds$")]
    public void GivenExport(int seconds)
    {
        fixture = new ControlFixture();
        drain = TimeSpan.FromSeconds(seconds);
        requests = fixture.Limits.RequestsPerSession;
    }

    [Given(@"^a node session with ""(.*)"" open for writing$")]
    public Task GivenSession(string name) => OpenAsync();

    [Given(@"^the session admits (\d+) requests$")]
    public Task GivenAdmits(int count)
    {
        requests = count;
        return OpenAsync();
    }

    [Given(@"^a write to ""(.*)"" that finishes when it is released$")]
    public void GivenWrite(string name) => StartWrite(2);

    [Given(@"^a write to ""(.*)"" that finishes when it is released or cancelled$")]
    public void GivenCooperativeWrite(string name)
    {
        honoursCancellation = true;
        StartWrite(2);
    }

    [Given(@"^2 writes to ""(.*)"" that finish when they are released$")]
    public void GivenTwoWrites(string name)
    {
        StartWrite(2);
        StartWrite(3);
    }

    [When("the node flushes an unused tag")]
    public void WhenFlushUnused() => Flush(150);

    [When("the node flushes its own tag")]
    public void WhenFlushItself() => flushes.Add(Send(NinePMessage.NewMsgTflush(new Tflush(201, 201))));

    [When("the node flushes the write's flush")]
    public void WhenFlushFlush()
    {
        Flush(100);
        Flush(FlushTag(0));
    }

    [When("the node flushes the write")]
    [When("the node flushes the write again")]
    public void WhenFlushWrite() => Flush(100);

    [When("the node flushes both writes")]
    public void WhenFlushBoth()
    {
        Flush(100);
        Flush(101);
    }

    [When("the node sends Tversion")]
    public void WhenVersion() => _ = Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000")));

    [When("the session closes")]
    public void WhenClose() => _ = dispatcher!.CloseSessionAsync(Session);

    [Then("that flush is answered with Rflush while the write still runs")]
    public async Task ThenAnsweredAtOnce()
    {
        Assert.IsType<Rflush>(await flushes[^1].WaitAsync(Wait));
        Assert.False(writes[0].IsCompleted);
    }

    [Then("the first flush finishes without a reply while the write still runs")]
    public async Task ThenSuperseded()
    {
        Assert.Same(FogNinePDispatcher.NoReply, await flushes[0].WaitAsync(Wait));
        Assert.False(writes[0].IsCompleted);
    }

    [Then("once the write is released the second flush is answered with Rflush")]
    public async Task ThenSecondAnswered()
    {
        releases[0].SetResult(1);
        Assert.IsType<Rflush>(await flushes[1].WaitAsync(Wait));
        Assert.IsType<Rwrite>(await writes[0].WaitAsync(Wait));
    }

    [Then("neither flush is refused while the writes still run")]
    public void ThenNotRefused() => Assert.All(flushes, flush => Assert.False(flush.IsCompleted));

    [Then("once the writes are released both flushes are answered with Rflush")]
    public async Task ThenBothAnswered()
    {
        foreach (var release in releases)
        {
            release.SetResult(1);
        }

        foreach (var flush in flushes)
        {
            Assert.IsType<Rflush>(await flush.WaitAsync(Wait));
        }
    }

    [Then("the flush is answered with Rflush")]
    public async Task ThenFlushed() => Assert.IsType<Rflush>(await flushes[0].WaitAsync(Wait));

    private static ushort FlushTag(int index) => (ushort)(201 + index);

    private async Task OpenAsync()
    {
        var tree = new ProbeTree
        {
            OnOpen = () =>
            {
                var release = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
                releases.Add(release);
                return new FogOpenFile(write: (_, _, cancellation) =>
                {
                    if (honoursCancellation)
                    {
                        cancellation.Register(() => release.TrySetCanceled(cancellation));
                    }

                    return release.Task;
                });
            },
        };
        releases.Clear();
        dispatcher = new FogNinePDispatcher(tree, fixture!.Policy, fixture.Limits with { Drain = drain, RequestsPerSession = requests });
        Assert.IsType<Rversion>(await Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000"))));
        Assert.IsType<Rattach>(await Send(NinePMessage.NewMsgTattach(new Tattach(1, 1, NinePConstants.NoFid, "worker", "runtime"))));
        for (uint fid = 2; fid <= 3; fid++)
        {
            Assert.IsType<Rwalk>(await Send(NinePMessage.NewMsgTwalk(new Twalk(1, 1, fid, ["file"]))));
            Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(1, fid, NinePConstants.OWRITE))));
        }
    }

    private void StartWrite(uint fid) => writes.Add(Send(NinePMessage.NewMsgTwrite(new Twrite((ushort)(98 + fid), fid, 0, new byte[] { 1 }))));

    private void Flush(ushort oldTag) => flushes.Add(Send(NinePMessage.NewMsgTflush(new Tflush(FlushTag(flushes.Count), oldTag))));

    private Task<object> Send(NinePMessage message) => dispatcher!.DispatchAsync(Session, message, NinePDialect.NineP2000, fixture!.NodeCertificate);
}
