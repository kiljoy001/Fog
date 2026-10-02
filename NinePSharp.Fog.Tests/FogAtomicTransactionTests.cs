using Xunit;
using static NinePSharp.Fog.Tests.FogTransactionTests;

namespace NinePSharp.Fog.Tests;

public sealed class FogAtomicTransactionTests
{
    [Fact]
    public async Task AtomicPreparationAndEffectAreSerializedAcrossDifferentTransactions()
    {
        var store = Store();
        string first = store.Clone("alice");
        string second = store.Clone("alice");
        Upload(store, first, "request", [1]);
        Upload(store, second, "request", [2]);
        int version = 0;
        FogAtomicPlan Prepare(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> inputs)
        {
            int observed = version;
            return new(new Dictionary<string, byte[]> { ["reply"] = [(byte)observed, inputs["request"].Span[0]] }, () => version = observed + 1);
        }

        await Bounded(Task.WhenAll(
            Task.Run(() => store.CommitAtomicAsync("alice", first, Prepare)),
            Task.Run(() => store.CommitAtomicAsync("alice", second, Prepare))));
        Assert.Equal(2, version);
        Assert.Equal(new byte[] { 0, 1 }, new[] { store.SnapshotOutput("alice", first, "reply")[0], store.SnapshotOutput("alice", second, "reply")[0] }.Order().ToArray());
        Assert.Equal(1, store.SnapshotOutput("alice", first, "reply")[1]);
        Assert.Equal(2, store.SnapshotOutput("alice", second, "reply")[1]);
        await Bounded(store.CommitAtomicAsync("alice", first, _ => throw new InvalidOperationException("reprepared")));
        Assert.Equal(2, version);
    }

    [Fact]
    public async Task ValidationPrecedesAtomicEffectsAndOutputsOwnTheirStorage()
    {
        var clock = new ManualTime();
        var store = Store(clock);
        string id = store.Clone("alice");
        int effects = 0;
        byte[] reply = [1, 2];
        FogAtomicPlan Prepare(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> inputs) =>
            new(new Dictionary<string, byte[]> { ["reply"] = reply }, () => effects++, "conflict");
        Error("upload-open", () => store.CommitAtomicAsync("alice", id, Prepare));
        using (var upload = store.OpenInput("alice", id, "s", "request"))
        {
            upload.Write(0, [9]);
            Error("upload-open", () => store.CommitAtomicAsync("alice", id, Prepare));
            upload.Seal();
        }

        await Assert.ThrowsAsync<ArgumentNullException>(() => Bounded(store.CommitAtomicAsync("alice", id, null!)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitAtomicAsync("alice", id, Prepare, cancellation.Token));
        Error("snapshot-limit", () => store.CommitAtomicAsync("alice", id, _ =>
            new(new Dictionary<string, byte[]> { ["reply"] = new byte[65] }, () => effects++)));
        Error("not-ready", () => store.OutputSize("alice", id, "reply"));
        Assert.Equal(0, effects);
        clock.Advance(TimeSpan.FromSeconds(9));
        await Bounded(store.CommitAtomicAsync("alice", id, Prepare));
        Assert.Equal(0, RetainedInputCount(store, id));
        reply[0] = 99;
        byte[] snapshot = store.SnapshotOutput("alice", id, "reply");
        snapshot[1] = 99;
        Assert.Equal(new byte[] { 1, 2 }, store.SnapshotOutput("alice", id, "reply"));
        Assert.Equal(new FogTransactionStatus(id, "done", "conflict"), store.Status("alice", id));
        Assert.Equal(2, store.OutputSize("alice", id, "reply"));
        Error("invalid-request", () => store.OutputSize("alice", id, "missing"));
        Error("denied", () => store.OutputSize("bob", id, "reply"));
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Contains(id, store.LiveIds());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(store.LiveIds());
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task UncertainAtomicEffectIsRetainedAndNeverAppliedAgain()
    {
        var clock = new ManualTime();
        var store = Store(clock);
        string id = store.Clone("alice");
        Upload(store, id, "request", []);
        int effects = 0;
        FogAtomicPlan Prepare(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> inputs) =>
            new(new Dictionary<string, byte[]> { ["reply"] = [] }, () =>
            {
                effects++;
                throw new IOException("private detail");
            });
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<FogException>(() => Bounded(store.CommitAtomicAsync("alice", id, Prepare)))).Code);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Contains(id, store.LiveIds());
        Assert.Equal(new FogTransactionStatus(id, "committing", "unavailable"), store.Status("alice", id));
        await Assert.ThrowsAsync<FogException>(() => Bounded(store.CommitAtomicAsync("alice", id, Prepare)));
        Error("busy", () => store.Release("alice", id));
        Error("not-ready", () => store.SnapshotOutput("alice", id, "reply"));
        Assert.Equal(1, effects);
    }

    [Fact]
    public void AtomicPlanRequiresAnApplyDelegateBeforeChangingState()
    {
        var store = Store();
        string id = store.Clone("alice");
        Upload(store, id, "request", []);
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = store.CommitAtomicAsync(
                "alice",
                id,
                _ => new FogAtomicPlan(new Dictionary<string, byte[]> { ["reply"] = [] }, null!));
        });
        Assert.Equal("staging", store.Status("alice", id).State);
    }
}
