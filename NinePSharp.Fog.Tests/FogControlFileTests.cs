using System.Text;
using Xunit;

namespace NinePSharp.Fog.Tests;

public sealed class FogControlFileTests
{
    [Theory]
    [InlineData("")]
    [InlineData("commit")]
    [InlineData("commit\r\n")]
    [InlineData(" commit\n")]
    [InlineData("COMMIT\n")]
    [InlineData("commit\nrelease\n")]
    [InlineData("commit\n\n")]
    [InlineData("com")]
    [InlineData("mit\n")]
    [InlineData("release")]
    [InlineData("release\r\n")]
    [InlineData("release\n\0")]
    public async Task CommandsAreExactUnfragmentedTwritePayloads(string text)
    {
        var store = FogTransactionTests.Store();
        string id = store.Clone("alice");
        FogTransactionTests.Upload(store, id, "request", []);
        var ctl = new FogControlFile(store, _ => throw new InvalidOperationException("must not prepare"));
        Assert.Equal("invalid-request", (await Assert.ThrowsAsync<FogException>(() => ctl.WriteAsync("alice", id, Encoding.UTF8.GetBytes(text)))).Code);
        Assert.Equal("staging", store.Status("alice", id).State);
    }

    [Fact]
    public async Task CommitAcknowledgesSevenAndReleaseAcknowledgesEightBytes()
    {
        var store = FogTransactionTests.Store();
        string id = store.Clone("alice");
        FogTransactionTests.Upload(store, id, "request", []);
        int effects = 0;
        var ctl = new FogControlFile(store, _ => FogTransactionTests.Plan([], () => effects++));
        Assert.Equal(7U, await ctl.WriteAsync("alice", id, "commit\n"u8.ToArray()).WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(7U, await ctl.WriteAsync("alice", id, "commit\n"u8.ToArray()).WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(1, effects);
        Assert.Equal(8U, await ctl.WriteAsync("alice", id, "release\n"u8.ToArray()));
        Assert.Equal(1, effects);
        Assert.Equal("tx-expired", (await Assert.ThrowsAsync<FogException>(() => ctl.WriteAsync("alice", id, "commit\n"u8.ToArray()))).Code);
    }

    [Fact]
    public async Task CancelledReleaseDoesNotDiscardTheTransaction()
    {
        var store = FogTransactionTests.Store();
        string id = store.Clone("alice");
        var ctl = new FogControlFile(store, _ => FogTransactionTests.Plan([]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctl.WriteAsync("alice", id, "release\n"u8.ToArray(), new CancellationToken(true)));
        Assert.Equal("staging", store.Status("alice", id).State);
        Assert.Throws<ArgumentNullException>(() => new FogControlFile(null!, _ => FogTransactionTests.Plan([])));
        Assert.Throws<ArgumentNullException>(() => new FogControlFile(store, null!));
    }
}
