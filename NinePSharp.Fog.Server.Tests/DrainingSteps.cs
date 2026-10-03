using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

[Binding]
[Scope(Feature = "Draining in-flight 9P work is bounded and leaves unfinished work with an unknown outcome")]
public sealed class DrainingSteps
{
    private const string Session = "node";
    private readonly RecordingLogger logger = new();
    private readonly TaskCompletionSource<uint> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TcpClient> clients = new();
    private ControlFixture? fixture;
    private ProbeTree? tree;
    private FogNinePDispatcher? dispatcher;
    private Func<ulong, ReadOnlyMemory<byte>, CancellationToken, Task<uint>> write = (_, _, _) => Task.FromResult(1u);
    private Task<object>? inFlight;
    private Task? drain;
    private Task<object>? flush;
    private object? reply;
    private FogUserListener? listener;

    [Given(@"^a control export with a drain limit of (\d+) milliseconds$")]
    public void GivenExport(int milliseconds)
    {
        fixture = new ControlFixture();
        tree = new ProbeTree { OnOpen = () => new FogOpenFile(write: (offset, data, token) => write(offset, data, token)) };
        dispatcher = new FogNinePDispatcher(tree, fixture.Policy, fixture.Limits with { Drain = TimeSpan.FromMilliseconds(milliseconds) }, fixture.Time, logger);
    }

    [Given(@"^a node session with ""(.*)"" open for writing$")]
    public async Task GivenOpen(string name)
    {
        Assert.IsType<Rversion>(await Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000"))));
        Assert.IsType<Rattach>(await Send(NinePMessage.NewMsgTattach(new Tattach(1, 1, NinePConstants.NoFid, "worker", "runtime"))));
        Assert.IsType<Rwalk>(await Send(NinePMessage.NewMsgTwalk(new Twalk(2, 1, 2, [name]))));
        Assert.IsType<Ropen>(await Send(NinePMessage.NewMsgTopen(new Topen(3, 2, NinePConstants.OWRITE))));
    }

    [Given(@"^a write to ""(.*)"" that ignores cancellation is in flight$")]
    public Task GivenStubbornWrite(string name) => StartWrite((_, _, _) => never.Task);

    [Given(@"^a write to ""(.*)"" that honours cancellation is in flight$")]
    public Task GivenCooperativeWrite(string name) => StartWrite(async (_, _, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return 1;
    });

    [When("the session closes")]
    public void WhenClose() => drain = dispatcher!.CloseSessionAsync(Session);

    [When("the node sends Tversion")]
    public void WhenVersion() => drain = Send(NinePMessage.NewMsgTversion(new Tversion(NinePConstants.NoTag, 8192, "9P2000")));

    [When("the node flushes the write")]
    public void WhenFlush() => drain = flush = Send(NinePMessage.NewMsgTflush(new Tflush(101, 100)));

    [When(@"^the node writes to ""(.*)"" again$")]
    public async Task WhenWriteAgain(string name)
    {
        await drain!.WaitAsync(TimeSpan.FromSeconds(2));
        reply = await Send(NinePMessage.NewMsgTwrite(new Twrite(102, 2, 0, new byte[] { 2 }))).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [When("the node stats the root with the write's tag")]
    public async Task WhenStatWithSameTag()
    {
        await drain!.WaitAsync(TimeSpan.FromSeconds(2));
        reply = await Send(NinePMessage.NewMsgTstat(new Tstat(100, 1))).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Then("that request is answered with Rstat")]
    public void ThenRstat() => Assert.IsType<Rstat>(reply);

    [Then(@"^it finishes within (\d+) seconds$")]
    public Task ThenFinishes(int seconds) => drain!.WaitAsync(TimeSpan.FromSeconds(seconds));

    [Then(@"^the write is answered with Rerror ""(.*)""$")]
    public async Task ThenWriteAnswered(string error)
        => Assert.Equal(error, Assert.IsType<Rerror>(await inFlight!.WaitAsync(TimeSpan.FromSeconds(2))).Ename);

    [Then("the flush is answered with Rflush")]
    public async Task ThenRflush() => Assert.IsType<Rflush>(await flush!.WaitAsync(TimeSpan.FromSeconds(2)));

    [Then(@"^that write is answered with Rerror ""(.*)""$")]
    public void ThenThatWrite(string error) => Assert.Equal(error, Assert.IsType<Rerror>(reply).Ename);

    [Then("the write's outcome is logged as unknown")]
    [Then("the abandoned request is logged as unknown")]
    public void ThenLoggedUnknown() => Assert.Contains(logger.At(LogLevel.Warning), message => message.Contains("outcome is unknown", StringComparison.Ordinal));

    [Then("no outcome is logged as unknown")]
    public async Task ThenNothingUnknown()
    {
        await drain!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(logger.At(LogLevel.Warning), message => message.Contains("unknown", StringComparison.Ordinal));
    }

    [Given(@"^a user listener admitting (\d+) connections? with a drain limit of (\d+) milliseconds over a dispatcher that never answers$")]
    public void GivenListener(int connections, int milliseconds)
    {
        listener = new FogUserListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NeverAnswers(),
            logger,
            connections,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMilliseconds(milliseconds));
        listener.Start();
    }

    [Given("a client that sent a request and closed its connection")]
    public async Task GivenClosedClient()
    {
        using TcpClient client = await RequestAsync();
    }

    [Given("a client that sent a request and kept its connection open")]
    public async Task GivenOpenClient() => clients.Add(await RequestAsync());

    [Then(@"^a second client is served within (\d+) seconds$")]
    public async Task ThenSecondServed(int seconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(listener!.LocalEndpoint, deadline.Token);
            await SendAsync(client, new Tversion(1, 8192, "9P2000"), deadline.Token);
            var header = new byte[7];
            if (await client.GetStream().ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, deadline.Token) == header.Length)
            {
                Assert.Equal((byte)MessageTypes.Rversion, header[4]);
                return;
            }

            await Task.Delay(50, deadline.Token);
        }
    }

    [When("the listener is disposed")]
    public void WhenDisposed() => drain = listener!.DisposeAsync().AsTask();

    [Then(@"^disposal finishes within (\d+) seconds$")]
    public Task ThenDisposed(int seconds) => drain!.WaitAsync(TimeSpan.FromSeconds(seconds));

    [Then("the client's connection is closed")]
    public async Task ThenClientClosed()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var buffer = new byte[64];
        while (await clients[0].GetStream().ReadAsync(buffer, deadline.Token) != 0)
        {
        }
    }

    [Then("no connection had to be force-closed")]
    public void ThenNoneForced() => Assert.DoesNotContain(logger.At(LogLevel.Warning), message => message.Contains("force-closed", StringComparison.Ordinal));

    [AfterScenario]
    public async Task CleanUpAsync()
    {
        never.TrySetResult(1);
        foreach (TcpClient client in clients)
        {
            client.Dispose();
        }

        if (listener is not null)
        {
            await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        fixture?.Dispose();
    }

    private static async Task SendAsync(TcpClient client, ISerializable message, CancellationToken cancellation)
    {
        var bytes = new byte[message.Size];
        message.WriteTo(bytes);
        await client.GetStream().WriteAsync(bytes, cancellation);
    }

    private Task<object> Send(NinePMessage message) => dispatcher!.DispatchAsync(Session, message, NinePDialect.NineP2000, fixture!.NodeCertificate);

    private async Task StartWrite(Func<ulong, ReadOnlyMemory<byte>, CancellationToken, Task<uint>> behaviour)
    {
        write = behaviour;
        inFlight = Send(NinePMessage.NewMsgTwrite(new Twrite(100, 2, 0, new byte[] { 1 })));
        await Task.Delay(50);
        Assert.False(inFlight.IsCompleted);
    }

    private async Task<TcpClient> RequestAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var client = new TcpClient();
        await client.ConnectAsync(listener!.LocalEndpoint, deadline.Token);
        await SendAsync(client, new Tversion(1, 8192, "9P2000"), deadline.Token);
        await client.GetStream().ReadExactlyAsync(new byte[7], deadline.Token);
        await SendAsync(client, new Tclunk(2, 1), deadline.Token);
        return client;
    }

    // Answers Tversion so a connection can start, and nothing after it.
    private sealed class NeverAnswers : INinePFSDispatcher
    {
        public Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
            => message is NinePMessage.MsgTversion version
                ? Task.FromResult<object>(new Rversion(version.Item.Tag, version.Item.MSize, "9P2000"))
                : new TaskCompletionSource<object>().Task;
    }
}
