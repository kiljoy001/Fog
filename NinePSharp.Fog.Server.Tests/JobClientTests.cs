using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Fog.Server;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class JobClientTests
{
    [Fact]
    public async Task RealTlsClientReconnectsToTheSameJobAndReplaysStart()
    {
        using var fixture = new ControlFixture();
        var runner = new JobTreeTests.ControlledRunner();
        await using var tree = new FogJobFileTree(runner, fixture.Policy.IsCurrentOwner);
        var dispatcher = new FogNinePDispatcher(tree, fixture.Policy, new(4, 32, 8, 8192, 262144, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)));
        await using var listener = new FogNodeListener(new IPEndPoint(IPAddress.Loopback, 0), fixture.ServerCertificate,
            fixture.Policy, dispatcher, NullLogger.Instance, 4, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<FogJobClient> Connect() => await FogJobClient.ConnectAsync(await FogTlsClient.ConnectAsync(listener.LocalEndpoint,
            "control.test", FogNodePolicy.SpkiPin(fixture.ServerCertificate), fixture.NodeCertificate, timeout.Token), "worker", timeout.Token);
        string id;
        await using (var first = await Connect())
        {
            id = await first.CloneAsync(timeout.Token);
            await first.UploadAsync(id, new("evaluate", null, 5000, 15000, 268435456, 65536), new byte[24000], timeout.Token);
            await first.ControlAsync(id, "start", timeout.Token);
            await runner.Started.Task.WaitAsync(timeout.Token);
        }
        await using var second = await Connect();
        Assert.Equal("running", (await second.StatusAsync(id, timeout.Token))["state"]);
        await second.ControlAsync(id, "start", timeout.Token);
        byte[] expected = Enumerable.Range(0, 24000).Select(n => (byte)n).ToArray();
        runner.Done.SetResult(new(expected, null, 7));
        Assert.Equal(expected, await second.WaitAsync(id, timeout.Token));
        Assert.Equal(1, runner.Calls);
        await second.ControlAsync(id, "release", timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => second.StatusAsync(id, timeout.Token));
    }
}
