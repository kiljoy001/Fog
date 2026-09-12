using System.Text;
using FsCheck.Xunit;
using Xunit;

namespace NinePSharp.Fog.Tests;

public sealed class FogTransactionTests
{
    internal static FogTransactionLimits Limits => new(4, 2, 64, 64, 512, 2, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

    [Fact]
    public async Task FrozenCommitRunsOnceAcrossConcurrentCallsAndLostReplies()
    {
        var store = Store();
        string id = store.Clone("alice");
        Upload(store, id, "request", [1, 2]);
        Upload(store, id, "payload", [3]);
        int effects = 0;
        int preparations = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] source = [4, 5];
        FogCommitPlan Prepare(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> inputs)
        {
            preparations++;
            Assert.Equal(new byte[] { 1, 2 }, inputs["request"].ToArray());
            Assert.Equal(new byte[] { 3 }, inputs["payload"].ToArray());
            return new(new Dictionary<string, byte[]> { ["reply"] = source }, async () =>
            {
                Interlocked.Increment(ref effects);
                started.SetResult();
                await unblock.Task;
            });
        }

        using var cancellation = new CancellationTokenSource();
        Task first = store.CommitAsync("alice", id, Prepare, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task duplicate = store.CommitAsync("alice", id, Prepare);
        Assert.Equal("committing", store.Status("alice", id).State);
        Error("busy", () => store.Release("alice", id));
        Error("busy", () => store.OpenInput("alice", id, "session", "request"));
        Error("not-ready", () => store.ReadOutput("alice", id, "reply", 0, 2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        source[0] = 99;
        unblock.SetResult();
        await duplicate;
        await store.CommitAsync("alice", id, Prepare);
        Assert.Equal(1, effects);
        Assert.Equal(1, preparations);
        Assert.Equal(new byte[] { 4, 5 }, store.ReadOutput("alice", id, "reply", 0, 2));
        Assert.Equal(new FogTransactionStatus(id, "done", null), store.Status("alice", id));
    }

    [Fact]
    public async Task ValidationAndOversizedSnapshotFailBeforeAnyEffect()
    {
        var store = Store();
        string id = store.Clone("alice");
        Error("upload-open", () => store.CommitAsync("alice", id, _ => Plan([])));
        using FogUpload upload = store.OpenInput("alice", id, "session", "request");
        upload.Write(0, [1]);
        Error("upload-open", () => store.CommitAsync("alice", id, _ => Plan([])));
        upload.Seal();
        int effects = 0;
        Error("invalid-request", () => store.CommitAsync("alice", id, _ => throw new FogException("invalid-request")));
        Error("snapshot-limit", () => store.CommitAsync("alice", id, _ => Plan(new byte[65], () => effects++)));
        Assert.Equal("staging", store.Status("alice", id).State);
        Assert.Equal(0, effects);
        await store.CommitAsync("alice", id, _ => Plan(new byte[64], () => effects++));
        Assert.Equal(1, effects);
        Assert.Equal(64, store.ReadOutput("alice", id, "reply", 0, uint.MaxValue).Length);
    }

    [Fact]
    public async Task UncertainEffectsAreNeverRetriedExpiredOrReleased()
    {
        var clock = new ManualTime();
        var store = Store(clock);
        string id = store.Clone("alice");
        Upload(store, id, "request", []);
        int effects = 0;
        FogCommitPlan Prepare(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _) => Plan([], () =>
        {
            effects++;
            throw new IOException("secret detail");
        });
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<FogException>(() => store.CommitAsync("alice", id, Prepare))).Message);
        clock.Advance(TimeSpan.FromDays(1));
        store.Sweep();
        Assert.Equal(new FogTransactionStatus(id, "committing", "unavailable"), store.Status("alice", id));
        await Assert.ThrowsAsync<FogException>(() => store.CommitAsync("alice", id, Prepare));
        Error("busy", () => store.Release("alice", id));
        Error("not-ready", () => store.ReadOutput("alice", id, "reply", 0, 1));
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task CompletedSnapshotsOwnTheirBytesAcrossFilesAndReads()
    {
        var store = Store();
        string id = store.Clone("alice");
        Upload(store, id, "request", []);
        byte[] reply = [1, 2, 3];
        byte[] payload = [4, 5];
        var outputs = new Dictionary<string, byte[]> { ["reply"] = reply, ["payload"] = payload };
        await store.CommitAsync("alice", id, _ => new(outputs, () => Task.CompletedTask));
        reply[0] = 99;
        payload[0] = 99;
        outputs.Clear();
        byte[] read = store.ReadOutput("alice", id, "reply", 0, uint.MaxValue);
        read[1] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, store.ReadOutput("alice", id, "reply", 0, uint.MaxValue));
        Assert.Equal(new byte[] { 2 }, store.ReadOutput("alice", id, "reply", 1, 1));
        Assert.Equal(new byte[] { 4, 5 }, store.ReadOutput("alice", id, "payload", 0, uint.MaxValue));
        Assert.Empty(store.ReadOutput("alice", id, "reply", 3, 1));
        Assert.Empty(store.ReadOutput("alice", id, "reply", ulong.MaxValue, uint.MaxValue));
        Assert.Empty(store.ReadOutput("alice", id, "reply", 0, 0));
        Error("invalid-request", () => store.ReadOutput("alice", id, "missing", 0, 1));
    }

    [Fact]
    public async Task RetainedResultsRequireCurrentOwnerAuthority()
    {
        bool allowed = true;
        var store = new FogTransactionStore(Limits, _ => allowed);
        string id = store.Clone("alice");
        Upload(store, id, "request", []);
        await store.CommitAsync("alice", id, _ => Plan([]));
        Error("denied", () => store.Status("bob", id));
        Error("denied", () => store.ReadOutput("bob", id, "reply", 0, 1));
        Error("denied", () => store.CommitAsync("bob", id, _ => Plan([])));
        Error("denied", () => store.Release("bob", id));
        allowed = false;
        Error("denied", () => store.Clone("alice"));
        Error("denied", () => store.Status("alice", id));
        Error("denied", () => store.ReadOutput("alice", id, "reply", 0, 1));
        Error("denied", () => store.CommitAsync("alice", id, _ => Plan([])));
        Error("denied", () => store.Release("alice", id));
        Error("denied", () => store.OpenInput("alice", id, "s", "request"));
    }

    [Fact]
    public async Task RevocationWhileWaitingDoesNotExposeACompletedOutcome()
    {
        bool allowed = true;
        var store = new FogTransactionStore(Limits, _ => allowed);
        string id = store.Clone("alice");
        Upload(store, id, "request", []);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task commit = store.CommitAsync("alice", id, _ => new(new Dictionary<string, byte[]> { ["reply"] = [] }, () => unblock.Task));
        allowed = false;
        unblock.SetResult();
        Assert.Equal("denied", (await Assert.ThrowsAsync<FogException>(() => commit)).Code);
    }

    [Fact]
    public void UploadOffsetsSingleWriterCapacityAndTerminalSessionLoss()
    {
        var store = Store();
        string id = store.Clone("alice");
        Upload(store, id, "request", [7]);
        using FogUpload replacement = store.OpenInput("alice", id, "s", "request");
        Error("busy", () => store.OpenInput("alice", id, "other", "payload"));
        replacement.Write(0, [1, 2]);
        Error("invalid-request", () => replacement.Write(1, [9]));
        Error("invalid-request", () => replacement.Write(3, [9]));
        Error("invalid-request", () => replacement.Write(ulong.MaxValue, [9]));
        Error("limit", () => replacement.Write(2, new byte[62]));
        replacement.Write(2, [3]);
        store.CloseSession("another");
        replacement.Write(3, []);
        store.CloseSession("s");
        Error("upload-open", () => replacement.Write(3, [4]));
        Error("upload-open", replacement.Seal);
        store.CommitAsync("alice", id, inputs =>
        {
            Assert.Equal(new byte[] { 7 }, inputs["request"].ToArray());
            return Plan([]);
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SuccessfulReplacementAndEmptyUploadsSealOnlyOnClunk()
    {
        var store = Store();
        string id = store.Clone("alice");
        Upload(store, id, "request", [1]);
        using (FogUpload aborted = store.OpenInput("alice", id, "s", "request"))
        {
            aborted.Write(0, [9]);
        }

        using FogUpload replacement = store.OpenInput("alice", id, "s", "request");
        replacement.Write(0, []);
        replacement.Seal();
        Error("upload-open", replacement.Seal);
        Error("upload-open", () => replacement.Write(0, [9]));
        Upload(store, id, "payload", new byte[64]);
        Error("limit", () => store.OpenInput("alice", id, "s", "third"));
        await store.CommitAsync("alice", id, inputs =>
        {
            Assert.Empty(inputs["request"].ToArray());
            Assert.Equal(64, inputs["payload"].Length);
            return Plan([]);
        });
    }

    [Fact]
    public void CleanupStillWorksAfterRevocationAndDoesNotAffectAReplacementWriter()
    {
        bool allowed = true;
        var store = new FogTransactionStore(Limits, _ => allowed);
        string id = store.Clone("alice");
        FogUpload old = store.OpenInput("alice", id, "s", "request");
        old.Write(0, [1]);
        allowed = false;
        Error("denied", () => old.Write(1, [2]));
        Error("denied", old.Seal);
        old.Dispose();
        allowed = true;
        using FogUpload current = store.OpenInput("alice", id, "s", "request");
        old.Dispose();
        current.Write(0, [3]);
        current.Seal();
        store.Release("alice", id);
        current.Dispose();
        Error("tx-expired", () => current.Write(0, []));
    }

    [Fact]
    public async Task ExpiryAndReleaseNeverRecreateKnownIdentities()
    {
        var clock = new ManualTime();
        var store = Store(clock);
        string first = store.Clone("alice");
        using FogUpload stale = store.OpenInput("alice", first, "s", "request");
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("staging", store.Status("alice", first).State);
        clock.Advance(TimeSpan.FromSeconds(1));
        Error("tx-expired", () => store.Status("alice", first));
        Error("tx-expired", () => stale.Write(0, []));
        string second = store.Clone("alice");
        Assert.EndsWith("-1", first);
        Assert.Equal(first[..^1] + "2", second);
        Upload(store, second, "request", []);
        await store.CommitAsync("alice", second, _ => Plan([]));
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Equal("done", store.Status("alice", second).State);
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Sweep();
        Error("tx-expired", () => store.CommitAsync("alice", second, _ => Plan([])));
        Error("tx-expired", () => store.OpenInput("alice", second, "s", "request"));
        string third = store.Clone("alice");
        store.Release("alice", third);
        Error("tx-expired", () => store.Release("alice", third));
        Error("tx-expired", () => store.Status("alice", "invented"));
        Assert.NotEqual(first[..64], Store().Clone("alice")[..64]);
        Assert.Matches("^[a-f0-9]{64}-[1-9][0-9]*$", third);
    }

    [Fact]
    public void CountAndByteReservationsAreReclaimedWithoutASpareSlot()
    {
        var clock = new ManualTime();
        var store = Store(clock);
        string first = store.Clone("alice");
        _ = store.Clone("alice");
        Error("limit", () => store.Clone("alice"));
        _ = store.Clone("bob");
        _ = store.Clone("bob");
        Error("limit", () => store.Clone("charlie"));
        store.Release("alice", first);
        _ = store.Clone("charlie");
        clock.Advance(TimeSpan.FromSeconds(10));
        _ = store.Clone("dave");
        _ = store.Clone("dave");

        var bytes = new FogTransactionStore(Limits with { MaxReservedBytes = 255 }, _ => true);
        string id = bytes.Clone("alice");
        Error("limit", () => bytes.Clone("bob"));
        bytes.Release("alice", id);
        _ = bytes.Clone("bob");
        var exact = new FogTransactionStore(Limits with { MaxReservedBytes = 256 }, _ => true);
        _ = exact.Clone("alice");
        _ = exact.Clone("bob");
        Error("limit", () => exact.Clone("charlie"));
    }

    [Fact]
    public async Task SemanticRejectionCompletesAndRetainsItsOutcome()
    {
        var store = Store();
        string id = store.Clone("alice");
        Upload(store, id, "request", []);
        await store.CommitAsync("alice", id, _ => new(new Dictionary<string, byte[]> { ["reply"] = [0] }, () => Task.CompletedTask, "conflict"));
        Assert.Equal("done", store.Status("alice", id).State);
        Assert.Equal("conflict", store.Status("alice", id).Error);
        await store.CommitAsync("alice", id, _ => throw new InvalidOperationException());
        store.Release("alice", id);
        Error("tx-expired", () => store.ReadOutput("alice", id, "reply", 0, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../request")]
    [InlineData("Bad")]
    [InlineData("é")]
    public void FileNamesAreBoundedSafeComponents(string file)
    {
        var store = Store();
        string id = store.Clone("alice");
        Error("invalid-request", () => store.OpenInput("alice", id, "s", file));
        Upload(store, id, "request", []);
        Error("invalid-request", () => store.CommitAsync("alice", id, _ => new(new Dictionary<string, byte[]> { ["reply"] = [], [file] = [] }, () => Task.CompletedTask)));
        Error("invalid-request", () => store.CommitAsync("alice", id, _ => new(new Dictionary<string, byte[]> { ["reply"] = [] }, () => Task.CompletedTask, file)));
    }

    [Fact]
    public void InvalidPlansAndNameLengthBoundaries()
    {
        var store = Store();
        string id = store.Clone("alice");
        using (FogUpload valid = store.OpenInput("alice", id, "s", new string('x', 64)))
        {
            valid.Write(0, []);
        }

        Error("invalid-request", () => store.OpenInput("alice", id, "s", new string('x', 65)));
        using (FogUpload valid = store.OpenInput("alice", id, "s", "a_0-z"))
        {
            valid.Write(0, []);
        }

        Upload(store, id, "request", []);
        Error("invalid-request", () => store.CommitAsync("alice", id, _ => new(new Dictionary<string, byte[]>(), () => Task.CompletedTask)));
        Error("invalid-request", () => store.CommitAsync("alice", id, _ => new(new Dictionary<string, byte[]> { ["reply"] = [], ["a"] = [], ["b"] = [] }, () => Task.CompletedTask)));
        Assert.Throws<ArgumentNullException>(() => { _ = store.CommitAsync("alice", id, _ => new(new Dictionary<string, byte[]> { ["reply"] = [] }, null!)); });
        Assert.Throws<ArgumentNullException>(() => { _ = store.CommitAsync("alice", id, null!); });
        Assert.Throws<ArgumentException>(() => store.OpenInput("alice", id, "", "request"));
        Error("denied", () => store.Clone(""));
        Assert.ThrowsAny<OperationCanceledException>(() => { _ = store.CommitAsync("alice", id, _ => throw new InvalidOperationException(), new CancellationToken(true)); });
        Assert.Equal("staging", store.Status("alice", id).State);
    }

    public static IEnumerable<object[]> InvalidLimits()
    {
        yield return [Limits with { MaxTransactions = 0 }];
        yield return [Limits with { MaxPerOwner = 0 }];
        yield return [Limits with { MaxInputBytes = 0 }];
        yield return [Limits with { MaxSnapshotBytes = 0 }];
        yield return [Limits with { MaxReservedBytes = 127 }];
        yield return [Limits with { MaxFiles = 0 }];
        yield return [Limits with { StagingLifetime = TimeSpan.Zero }];
        yield return [Limits with { RetentionLifetime = TimeSpan.Zero }];
    }

    [Theory]
    [MemberData(nameof(InvalidLimits))]
    public void LimitsMustBeFiniteAndPositive(FogTransactionLimits limits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FogTransactionStore(limits, _ => true));

    [Fact]
    public void RequiredDependenciesCannotBeNull()
    {
        Assert.Throws<ArgumentNullException>(() => new FogTransactionStore(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => new FogTransactionStore(Limits, null!));
    }

    [Property(MaxTest = 150)]
    public bool FragmentationDoesNotChangeSealedRequestOrExecuteBeforeCommit(byte[] data, byte stride)
    {
        byte[] input = data.Take(64).ToArray();
        var store = Store();
        string id = store.Clone("alice");
        using FogUpload upload = store.OpenInput("alice", id, "s", "request");
        for (int offset = 0; offset < input.Length; offset += stride + 1)
        {
            upload.Write((ulong)offset, input.AsSpan(offset, Math.Min(stride + 1, input.Length - offset)));
        }

        upload.Seal();
        int effects = 0;
        store.CommitAsync("alice", id, inputs => Plan(inputs["request"].ToArray(), () => effects++)).GetAwaiter().GetResult();
        store.CommitAsync("alice", id, _ => throw new InvalidOperationException()).GetAwaiter().GetResult();
        return effects == 1 && input.SequenceEqual(store.ReadOutput("alice", id, "reply", 0, uint.MaxValue));
    }

    internal static FogTransactionStore Store(TimeProvider? time = null) => new(Limits, _ => true, time);

    internal static void Upload(FogTransactionStore store, string id, string file, byte[] bytes)
    {
        using FogUpload upload = store.OpenInput("alice", id, "session", file);
        upload.Write(0, bytes);
        upload.Seal();
    }

    internal static FogCommitPlan Plan(byte[] reply, Action? effect = null) => new(
        new Dictionary<string, byte[]> { ["reply"] = reply }, () => { effect?.Invoke(); return Task.CompletedTask; });

    internal static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<FogException>(action).Code);

    internal sealed class ManualTime : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}
