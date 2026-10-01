using System.Reflection;
using Xunit;

namespace NinePSharp.Fog.Tests;

public sealed class FogBoundaryTests
{
    [Fact]
    public async Task SnapshotLimitAppliesToTheSumOfAllFiles()
    {
        var store = FogTransactionTests.Store();
        string id = store.Clone("alice");
        FogTransactionTests.Upload(store, id, "request", []);
        int effects = 0;
        FogCommitPlan Plan(int second) => new(new Dictionary<string, byte[]> { ["reply"] = new byte[32], ["extra"] = new byte[second] },
            () => { effects++; return Task.CompletedTask; });
        FogTransactionTests.Error("snapshot-limit", () => store.CommitAsync("alice", id, _ => Plan(33)));
        Assert.Equal(0, effects);
        await FogTransactionTests.Bounded(store.CommitAsync("alice", id, _ => Plan(32)));
        Assert.Equal(1, effects);
        Assert.Equal(32, store.ReadOutput("alice", id, "extra", 0, 64).Length);
        Assert.Equal(16, store.ReadOutput("alice", id, "reply", 16, uint.MaxValue).Length);
    }

    [Fact]
    public void TransactionCountIsIndependentOfOwnerAndByteLimits()
    {
        var store = new FogTransactionStore(FogTransactionTests.Limits with { MaxTransactions = 1, MaxPerOwner = 4 }, _ => true);
        _ = store.Clone("alice");
        FogTransactionTests.Error("limit", () => store.Clone("bob"));
    }

    [Fact]
    public void CounterExhaustionFailsBeforeReusingAnIdentityOrReservation()
    {
        var store = FogTransactionTests.Store();
        // Fault injection at an otherwise unreachable uint64 boundary, not a production escape hatch.
        FieldInfo sequence = typeof(FogTransactionStore).GetField("sequence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sequence.SetValue(store, ulong.MaxValue - 1);
        Assert.EndsWith("-18446744073709551615", store.Clone("alice"));
        FogTransactionTests.Error("unavailable", () => store.Clone("alice"));
        Assert.Equal(ulong.MaxValue, sequence.GetValue(store));
    }

    [Fact]
    public void SchemaValidationChecksEveryColumnAndAsciiBoundary()
    {
        _ = new FogRecordSchema("z9", ["a", "z9"], [], ["a"]);
        Assert.Throws<ArgumentException>(() => new FogRecordSchema("good", ["id", "BAD"], [], ["id"]));
        Assert.Throws<ArgumentNullException>(() => new FogRecordSchema("good", ["id"], null!, []));
    }

    [Fact]
    public void AllUploadCleanupPathsCloseTheUnderlyingBuffer()
    {
        var clock = new FogTransactionTests.ManualTime();
        var store = FogTransactionTests.Store(clock);
        string id = store.Clone("alice");
        FogUpload upload = store.OpenInput("alice", id, "s", "z9");
        Assert.True(Buffer(upload).CanWrite);
        upload.Seal();
        Assert.False(Buffer(upload).CanWrite);
        upload = store.OpenInput("alice", id, "s", "request");
        store.CloseSession("s");
        Assert.False(Buffer(upload).CanWrite);
        upload = store.OpenInput("alice", id, "s", "request");
        upload.Dispose();
        Assert.False(Buffer(upload).CanWrite);
        upload = store.OpenInput("alice", id, "s", "request");
        store.Release("alice", id);
        Assert.False(Buffer(upload).CanWrite);
        id = store.Clone("alice");
        upload = store.OpenInput("alice", id, "s", "request");
        clock.Advance(TimeSpan.FromSeconds(10));
        store.Sweep();
        Assert.False(Buffer(upload).CanWrite);
        id = store.Clone("alice");
        upload = store.OpenInput("alice", id, "s", "request");
        clock.Advance(TimeSpan.FromSeconds(10));
        FogTransactionTests.Error("tx-expired", () => store.Status("alice", id));
        Assert.False(Buffer(upload).CanWrite);
    }

    [Fact]
    public void NegativeLimitsCannotBypassZeroChecks()
    {
        FogTransactionLimits limits = FogTransactionTests.Limits;
        foreach (var invalid in new[]
        {
            limits with { MaxTransactions = -1 }, limits with { MaxPerOwner = -1 },
            limits with { MaxInputBytes = -1 }, limits with { MaxSnapshotBytes = -1 },
            limits with { MaxFiles = -1 }, limits with { MaxReservedBytes = -1 },
            limits with { StagingLifetime = TimeSpan.FromTicks(-1) }, limits with { RetentionLifetime = TimeSpan.FromTicks(-1) },
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FogTransactionStore(invalid, _ => true));
        }
    }

    private static MemoryStream Buffer(FogUpload upload) =>
        (MemoryStream)typeof(FogUpload).GetField("Buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(upload)!;
}
