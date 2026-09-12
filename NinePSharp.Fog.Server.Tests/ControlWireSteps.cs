using System.Security.Authentication;
using NinePSharp.Fog.Server;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

[Binding]
public sealed class ControlWireSteps
{
    private ControlFixture fixture = null!;
    private FogNodeListener listener = null!;
    private byte[] input = [];
    private IReadOnlyDictionary<string, byte[]> result = null!;
    private Exception? rejection;

    [Given("an enrolled node connected to the direct TLS 1.3 control listener")]
    public void Listener()
    {
        fixture = new ControlFixture();
        listener = fixture.Listen();
    }

    [When("the node submits a 3000 byte control request using 256 byte 9P messages")]
    public async Task Submit()
    {
        input = Enumerable.Range(0, 3000).Select(value => (byte)value).ToArray();
        var client = new FogTransactionClient(async cancellation => await FogTlsClient.ConnectAsync(listener.LocalEndpoint,
            "control.test", FogNodePolicy.SpkiPin(fixture.ServerCertificate), fixture.NodeCertificate, cancellation),
            "worker", 256, 4096, TimeSpan.FromSeconds(10));
        result = await client.ExecuteAsync("fixture", new Dictionary<string, byte[]> { ["request"] = input }, ["reply"]);
    }

    [Then("its immutable reply matches all 3000 input bytes")]
    public void Reply() => Assert.Equal(input, result["reply"]);

    [Then("the host applied the transaction exactly once")]
    public void Applied() => Assert.Equal(1, fixture.Effects);

    [Then("no transaction reservation remains after the result is received")]
    public void Released() => Assert.Empty(fixture.Store.LiveIds());

    [When("the node connects using an incorrect expected server name")]
    public async Task BadName()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        rejection = await Record.ExceptionAsync(async () =>
        {
            await using var tls = await FogTlsClient.ConnectAsync(listener.LocalEndpoint, "wrong.test",
                FogNodePolicy.SpkiPin(fixture.ServerCertificate), fixture.NodeCertificate, deadline.Token);
        });
    }

    [Then("TLS rejects the connection before any transaction effect")]
    public void Rejected() { Assert.IsType<AuthenticationException>(rejection); Assert.Equal(0, fixture.Effects); }

    [AfterScenario]
    public async Task Cleanup()
    {
        if (listener is not null) await listener.DisposeAsync();
        fixture?.Dispose();
    }
}
