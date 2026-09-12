using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;

namespace NinePSharp.Fog;

/// <summary>
/// Host-local ephemeral transaction core. Identity/session arguments come from a trusted
/// authenticated adapter, never an unverified uname. No listener or implicit enrollment.
/// </summary>
public sealed class FogTransactionStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly FogTransactionLimits limits;
    private readonly Func<string, bool> authorize;
    private readonly TimeProvider time;
    private readonly string boot = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private ulong sequence;
    private long reservedBytes;

    public FogTransactionStore(FogTransactionLimits limits, Func<string, bool> authorize, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(authorize);
        limits.Validate();
        this.limits = limits;
        this.authorize = authorize;
        this.time = time ?? TimeProvider.System;
    }

    public string Clone(string owner)
    {
        lock (gate)
        {
            CheckAuthority(owner);
            SweepCore();
            if (entries.Count >= limits.MaxTransactions || entries.Values.Count(entry => entry.Owner == owner) >= limits.MaxPerOwner ||
                reservedBytes > limits.MaxReservedBytes - limits.Reservation)
            {
                throw new FogException("limit");
            }

            if (sequence == ulong.MaxValue)
            {
                throw new FogException("unavailable");
            }

            string id = boot + "-" + (++sequence).ToString(CultureInfo.InvariantCulture);
            entries.Add(id, new Entry(owner, time.GetTimestamp()));
            reservedBytes += limits.Reservation;
            return id;
        }
    }

    public FogUpload OpenInput(string owner, string id, string session, string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(session);
        lock (gate)
        {
            Entry entry = Find(owner, id);
            RequireStaging(entry);
            if (!ValidFile(file))
            {
                throw new FogException("invalid-request");
            }

            if (entry.Writer is not null)
            {
                throw new FogException("busy");
            }

            if (!entry.Inputs.ContainsKey(file) && entry.Inputs.Count >= limits.MaxFiles)
            {
                throw new FogException("limit");
            }

            return entry.Writer = new FogUpload(this, owner, id, session, file);
        }
    }

    /// <summary>
    /// Cancellation stops only this caller's wait. An uncertain apply stays committing,
    /// retains its reservation, and is never retried or expired by this store.
    /// </summary>
    public Task CommitAsync(string owner, string id,
        Func<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>, FogCommitPlan> prepare,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        cancellationToken.ThrowIfCancellationRequested();
        Task outcome;
        lock (gate)
        {
            Entry entry = Find(owner, id);
            if (entry.State == "staging")
            {
                if (entry.Writer is not null || !entry.Inputs.ContainsKey("request"))
                {
                    throw new FogException("upload-open");
                }

                // Do not lend writable backing arrays to service preparation.
                var inputs = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(entry.Inputs.ToDictionary(
                    pair => pair.Key, pair => (ReadOnlyMemory<byte>)pair.Value.ToArray(), StringComparer.Ordinal));
                FogCommitPlan plan = prepare(inputs);
                Dictionary<string, byte[]> outputs = FreezeOutputs(plan);
                entry.State = "committing";
                entry.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // Invoke outside the store lock. Unrelated control and cleanup must remain responsive.
                _ = Task.Run(() => ApplyAsync(entry, plan, outputs), CancellationToken.None);
            }

            outcome = entry.Completion!.Task;
        }

        return ObserveAsync(owner, id, outcome, cancellationToken);
    }

    public FogTransactionStatus Status(string owner, string id)
    {
        lock (gate)
        {
            Entry entry = Find(owner, id);
            return new FogTransactionStatus(id, entry.State, entry.Error);
        }
    }

    /// <summary>Atomically prepares, bounds, applies and records an in-memory control operation.</summary>
    public Task CommitAtomicAsync(string owner, string id,
        Func<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>, FogAtomicPlan> prepare,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        cancellationToken.ThrowIfCancellationRequested();
        Task outcome;
        lock (gate)
        {
            Entry entry = Find(owner, id);
            if (entry.State == "staging")
            {
                if (entry.Writer is not null || !entry.Inputs.ContainsKey("request"))
                {
                    throw new FogException("upload-open");
                }

                var inputs = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(entry.Inputs.ToDictionary(
                    pair => pair.Key, pair => (ReadOnlyMemory<byte>)pair.Value.ToArray(), StringComparer.Ordinal));
                FogAtomicPlan plan = prepare(inputs);
                ArgumentNullException.ThrowIfNull(plan.Apply);
                var outputs = FreezeOutputs(new FogCommitPlan(plan.Outputs, () => Task.CompletedTask, plan.Error));
                entry.State = "committing";
                entry.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    plan.Apply();
                    entry.Outputs = outputs;
                    entry.Error = plan.Error;
                    entry.Inputs.Clear();
                    entry.Timestamp = time.GetTimestamp();
                    entry.State = "done";
                    entry.Completion.SetResult();
                }
                catch (Exception)
                {
                    entry.Error = "unavailable";
                    entry.Completion.SetException(new FogException("unavailable"));
                }
            }

            outcome = entry.Completion!.Task;
        }

        return ObserveAsync(owner, id, outcome, cancellationToken);
    }

    /// <summary>Copies one completed file for a separately bounded per-open snapshot reservation.</summary>
    public byte[] SnapshotOutput(string owner, string id, string file) => ReadOutput(owner, id, file, 0, uint.MaxValue);

    public int OutputSize(string owner, string id, string file)
    {
        lock (gate)
        {
            Entry entry = Find(owner, id);
            if (entry.State != "done") throw new FogException("not-ready");
            if (!entry.Outputs.TryGetValue(file, out var bytes)) throw new FogException("invalid-request");
            return bytes.Length;
        }
    }

    /// <summary>Host-side bounded metadata maintenance; this does not confer transaction access.</summary>
    public IReadOnlySet<string> LiveIds()
    {
        lock (gate)
        {
            SweepCore();
            return entries.Keys.ToHashSet(StringComparer.Ordinal);
        }
    }

    public byte[] ReadOutput(string owner, string id, string file, ulong offset, uint count)
    {
        lock (gate)
        {
            Entry entry = Find(owner, id);
            if (entry.State != "done")
            {
                throw new FogException("not-ready");
            }

            if (!entry.Outputs.TryGetValue(file, out byte[]? bytes))
            {
                throw new FogException("invalid-request");
            }

            return offset >= (ulong)bytes.Length ? [] : bytes.AsSpan((int)offset, (int)Math.Min(count, (ulong)bytes.Length - offset)).ToArray();
        }
    }

    public void Release(string owner, string id)
    {
        lock (gate)
        {
            Entry entry = Find(owner, id);
            if (entry.State == "committing")
            {
                throw new FogException("busy");
            }

            Remove(id, entry);
        }
    }

    /// <summary>Only terminal logical-session loss calls this; AAN suspension does not.</summary>
    public void CloseSession(string session)
    {
        lock (gate)
        {
            foreach (Entry entry in entries.Values)
            {
                if (entry.Writer is { } writer && writer.Session == session)
                {
                    writer.Buffer.Dispose();
                    entry.Writer = null;
                }
            }
        }
    }

    public void Sweep()
    {
        lock (gate)
        {
            SweepCore();
        }
    }

    internal void Write(FogUpload upload, ulong offset, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            Entry entry = FindWriter(upload);
            if (offset != (ulong)upload.Buffer.Length)
            {
                throw new FogException("invalid-request");
            }

            long accepted = entry.Inputs.Values.Sum(value => (long)value.Length) + upload.Buffer.Length;
            if (bytes.Length > limits.MaxInputBytes - accepted)
            {
                throw new FogException("limit");
            }

            upload.Buffer.Write(bytes);
        }
    }

    internal void Seal(FogUpload upload)
    {
        lock (gate)
        {
            Entry entry = FindWriter(upload);
            entry.Inputs[upload.File] = upload.Buffer.ToArray();
            entry.Writer = null;
            upload.Buffer.Dispose();
        }
    }

    internal void Abort(FogUpload upload)
    {
        lock (gate)
        {
            // Cleanup must still work after revocation or timeout, without allocating a slot.
            if (entries.TryGetValue(upload.Transaction, out Entry? entry) && ReferenceEquals(entry.Writer, upload))
            {
                entry.Writer = null;
            }

            upload.Buffer.Dispose();
        }
    }

    private async Task ApplyAsync(Entry entry, FogCommitPlan plan, Dictionary<string, byte[]> outputs)
    {
        try
        {
            await plan.ApplyAsync().ConfigureAwait(false);
            lock (gate)
            {
                entry.Outputs = outputs;
                entry.Error = plan.Error;
                entry.Inputs.Clear();
                entry.Timestamp = time.GetTimestamp();
                entry.State = "done";
                entry.Completion!.SetResult();
            }
        }
        catch (Exception)
        {
            lock (gate)
            {
                // Do not imply rollback, leak the exception, or release potentially live ownership.
                entry.Error = "unavailable";
                entry.Completion!.SetException(new FogException("unavailable"));
            }
        }
    }

    private async Task ObserveAsync(string owner, string id, Task outcome, CancellationToken cancellationToken)
    {
        await outcome.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            _ = Find(owner, id);
        }
    }

    private Dictionary<string, byte[]> FreezeOutputs(FogCommitPlan plan)
    {
        if (plan.Outputs.Count > limits.MaxFiles || !plan.Outputs.ContainsKey("reply") ||
            plan.Outputs.Keys.Any(file => !ValidFile(file)) || plan.Error is not null && !ValidFile(plan.Error))
        {
            throw new FogException("invalid-request");
        }

        if (plan.Outputs.Values.Sum(bytes => (long)bytes.Length) > limits.MaxSnapshotBytes)
        {
            throw new FogException("snapshot-limit");
        }

        ArgumentNullException.ThrowIfNull(plan.ApplyAsync);
        return plan.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    private Entry FindWriter(FogUpload upload)
    {
        Entry entry = Find(upload.Owner, upload.Transaction);
        RequireStaging(entry);
        if (!ReferenceEquals(entry.Writer, upload))
        {
            throw new FogException("upload-open");
        }

        return entry;
    }

    private Entry Find(string owner, string id)
    {
        CheckAuthority(owner);
        if (!entries.TryGetValue(id, out Entry? entry))
        {
            throw new FogException("tx-expired");
        }

        if (entry.Owner != owner)
        {
            throw new FogException("denied");
        }

        if (Expired(entry))
        {
            Remove(id, entry);
            throw new FogException("tx-expired");
        }

        return entry;
    }

    private void CheckAuthority(string owner)
    {
        if (string.IsNullOrEmpty(owner) || !authorize(owner))
        {
            throw new FogException("denied");
        }
    }

    private static void RequireStaging(Entry entry)
    {
        if (entry.State != "staging")
        {
            throw new FogException("busy");
        }
    }

    private static bool ValidFile(string file) => !string.IsNullOrEmpty(file) && file.Length <= 64 &&
        file.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private bool Expired(Entry entry) => entry.State != "committing" &&
        time.GetElapsedTime(entry.Timestamp) >= (entry.State == "done" ? limits.RetentionLifetime : limits.StagingLifetime);

    private void SweepCore()
    {
        foreach (var pair in entries.Where(pair => Expired(pair.Value)).ToArray())
        {
            Remove(pair.Key, pair.Value);
        }
    }

    private void Remove(string id, Entry entry)
    {
        entry.Writer?.Buffer.Dispose();
        entries.Remove(id);
        reservedBytes -= limits.Reservation;
    }

    private sealed class Entry(string owner, long timestamp)
    {
        internal readonly string Owner = owner;
        internal readonly Dictionary<string, byte[]> Inputs = new(StringComparer.Ordinal);
        internal Dictionary<string, byte[]> Outputs = new(StringComparer.Ordinal);
        internal long Timestamp = timestamp;
        internal string State = "staging";
        internal string? Error;
        internal FogUpload? Writer;
        internal TaskCompletionSource? Completion;
    }
}
