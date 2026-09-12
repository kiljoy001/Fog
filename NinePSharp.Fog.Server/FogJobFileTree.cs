using System.Security.Cryptography;
using System.Text;
using NinePSharp.Constants;

namespace NinePSharp.Fog.Server;

/// <summary>Bounded, process-lifetime job retention, with asynchronous start and immutable per-open results.</summary>
public sealed class FogJobFileTree : FogFileTree, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly string boot = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private readonly IFogMathRunner runner;
    private readonly Func<string, bool> authorized;
    private readonly TimeProvider time;
    private readonly int capacity;
    private readonly TimeSpan retention;
    private readonly FogFileNode compute;
    private readonly FogFileNode clone;
    private ulong sequence;
    private bool disposed;

    public FogJobFileTree(IFogMathRunner runner, Func<string, bool> authorized, int capacity = 16, TimeSpan? retention = null, TimeProvider? time = null)
    {
        if (capacity is < 1 or > 64 || retention <= TimeSpan.Zero) throw new ArgumentException("Invalid job limits.");
        this.runner = runner;
        this.authorized = authorized;
        this.capacity = capacity;
        this.retention = retention ?? TimeSpan.FromMinutes(10);
        this.time = time ?? TimeProvider.System;
        Root = new(AllocateQid(), "/", true);
        compute = new(AllocateQid(), "compute", true);
        clone = new(AllocateQid(), "clone", false, File: "clone");
    }

    public override FogFileNode Root { get; }

    public override void Check(FogPrincipal principal, FogFileNode node)
    {
        lock (gate)
        {
            if (disposed || !authorized(principal.Owner)) throw new FogException("denied");
            Prune();
            if (node.Transaction.Length != 0) _ = Find(principal.Owner, node.Transaction);
        }
    }

    public override FogFileNode Walk(FogPrincipal principal, FogFileNode directory, string name)
    {
        lock (gate)
        {
            Check(principal, directory);
            if (!directory.Directory) throw new FogException("invalid-request");
            if (name == ".") return directory;
            if (name == "..") return directory.Transaction.Length == 0 ? Root : compute;
            if (directory == Root && name == "compute") return compute;
            if (directory == compute) return name == "clone" ? clone : Find(principal.Owner, name).Directory;
            if (directory.Transaction.Length != 0 && Find(principal.Owner, directory.Transaction).Files.TryGetValue(name, out var file)) return file;
            throw new FogException("job-expired");
        }
    }

    public override IReadOnlyList<FogFileNode> List(FogPrincipal principal, FogFileNode directory)
    {
        lock (gate)
        {
            Check(principal, directory);
            if (directory == Root) return [compute];
            if (directory == compute) return [clone];
            return Find(principal.Owner, directory.Transaction).Files.Values.ToArray();
        }
    }

    public override FogOpenFile Open(FogPrincipal principal, string session, FogFileNode node, byte mode, long snapshotBudget)
    {
        lock (gate)
        {
            Check(principal, node);
            if (node == clone)
            {
                Mode(mode, NinePConstants.OREAD);
                if (snapshotBudget < 86) throw new FogException("snapshot-limit");
                if (jobs.Count >= capacity) throw new FogException("limit");
                if (sequence == long.MaxValue) throw new FogException("unavailable");
                string id = boot + "-" + FogMathJob.Decimal((long)++sequence);
                var directory = new FogFileNode(AllocateQid(), id, true, Transaction: id);
                var files = new[] { "spec", "input", "ctl", "status", "result" }.ToDictionary(name => name,
                    name => new FogFileNode(AllocateQid(), name, false, Transaction: id, File: name));
                jobs.Add(id, new Job(principal.Owner, directory, files, time.GetTimestamp()));
                return Snapshot(Encoding.ASCII.GetBytes(id + "\n"), snapshotBudget);
            }
            Job job = Find(principal.Owner, node.Transaction);
            if (node.File is "spec" or "input")
            {
                Mode(mode, NinePConstants.OWRITE);
                return Upload(job, session, node.File);
            }
            if (node.File == "ctl")
            {
                Mode(mode, NinePConstants.OWRITE);
                return new FogOpenFile(write: (_, bytes, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        Check(principal, node);
                        Control(job, bytes.Span);
                        return Task.FromResult((uint)bytes.Length);
                    }
                });
            }
            Mode(mode, NinePConstants.OREAD);
            if (node.File == "status") return Snapshot(Status(job), snapshotBudget);
            if (node.File != "result" || job.State != "succeeded") throw new FogException("not-ready");
            return Snapshot(job.Result.ToArray(), snapshotBudget);
        }
    }

    private FogOpenFile Upload(Job job, string session, string file)
    {
        if (job.State != "staging" || job.Writer is not null) throw new FogException("busy");
        var upload = new UploadState(session, file);
        job.Writer = upload;
        return new FogOpenFile(write: (offset, bytes, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (job.Writer != upload || job.State != "staging") throw new FogException("busy");
                int maximum = file == "spec" ? 8192 : 1048576;
                if (offset != (ulong)upload.Bytes.Length || bytes.Length > maximum - upload.Bytes.Length) throw new FogException("limit");
                upload.Bytes.Write(bytes.Span);
                return Task.FromResult((uint)bytes.Length);
            }
        }, close: seal =>
        {
            lock (gate)
            {
                if (job.Writer != upload) return;
                if (seal && job.State == "staging") job.Inputs[file] = upload.Bytes.ToArray();
                job.Writer = null;
                upload.Bytes.Dispose();
            }
        });
    }

    private void Control(Job job, ReadOnlySpan<byte> command)
    {
        if (command.SequenceEqual("start\n"u8))
        {
            if (job.State != "staging") return;
            if (job.Writer is not null || !job.Inputs.ContainsKey("input") || !job.Inputs.ContainsKey("spec")) throw new FogException("not-ready");
            FogMathJob spec = FogMathJob.Parse(job.Directory.Name, job.Inputs["spec"]);
            job.State = "queued";
            job.Stop.CancelAfter(spec.DeadlineMilliseconds);
            job.Work = Task.Run(() => Run(job, spec));
            return;
        }
        if (command.SequenceEqual("cancel\n"u8))
        {
            if (job.State is "queued" or "running") { job.CancelRequested = true; job.Stop.Cancel(); }
            else if (job.State == "staging") Finish(job, "cancelled", "cancelled", [], 0);
            return;
        }
        if (command.SequenceEqual("release\n"u8))
        {
            if (job.State is "queued" or "running") throw new FogException("busy");
            Remove(job);
            return;
        }
        throw new FogException("invalid-request");
    }

    private async Task Run(Job job, FogMathJob spec)
    {
        lock (gate) job.State = "running";
        FogMathOutcome outcome;
        try { outcome = await runner.RunAsync(spec, job.Inputs["input"], job.Stop.Token).ConfigureAwait(false); }
        catch (Exception) { outcome = new([], "worker-lost", 0); }
        lock (gate)
        {
            string? error = outcome.Error;
            if (error == "cleanup-failed") { }
            else if (!authorized(job.Owner)) error = "policy-changed";
            else if (job.CancelRequested) error = "cancelled";
            else if (job.Stop.IsCancellationRequested) error = "deadline";
            else if (outcome.Result.Length > spec.OutputBytes) error = "output-limit";
            Finish(job, error is null ? "succeeded" : error == "cancelled" ? "cancelled" : "failed", error,
                error is null ? outcome.Result.ToArray() : [], outcome.CpuMilliseconds);
        }
    }

    private void Finish(Job job, string state, string? error, byte[] result, long cpu)
    {
        job.State = state;
        job.Error = error;
        job.Result = result;
        job.Cpu = cpu;
        job.RetainedAt = time.GetTimestamp();
        job.Inputs.Clear();
    }

    private static byte[] Status(Job job) => FogMathJob.StatusSchema.Serialize([new Dictionary<string, string?>
    {
        ["job"] = job.Directory.Name, ["state"] = job.State, ["error"] = job.Error,
        ["result_bytes"] = FogMathJob.Decimal(job.Result.Length), ["p_cpu_ms_used"] = FogMathJob.Decimal(job.Cpu),
    }], 2048, 1);

    private Job Find(string owner, string id)
    {
        if (!jobs.TryGetValue(id, out Job? job)) throw new FogException("job-expired");
        if (job.Owner != owner) throw new FogException("denied");
        return job;
    }

    private void Prune()
    {
        foreach (Job job in jobs.Values.ToArray())
        {
            if (!authorized(job.Owner)) job.Stop.Cancel();
            if (job.State is not ("running" or "queued") && time.GetElapsedTime(job.RetainedAt) >= retention) Remove(job);
        }
    }

    private void Remove(Job job)
    {
        jobs.Remove(job.Directory.Name);
        job.Writer?.Bytes.Dispose();
        job.Writer = null;
        job.Stop.Dispose();
    }

    public override void CloseSession(string session)
    {
        lock (gate)
        {
            foreach (Job job in jobs.Values)
            {
                if (job.Writer?.Session != session) continue;
                job.Writer.Bytes.Dispose();
                job.Writer = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] active;
        lock (gate)
        {
            disposed = true;
            foreach (Job job in jobs.Values) job.Stop.Cancel();
            active = jobs.Values.Select(job => job.Work).ToArray();
        }
        await Task.WhenAll(active).ConfigureAwait(false);
        lock (gate) foreach (Job job in jobs.Values.ToArray()) Remove(job);
    }

    private static FogOpenFile Snapshot(byte[] bytes, long budget) => bytes.Length <= budget ? new(bytes) : throw new FogException("snapshot-limit");
    private static void Mode(byte actual, byte expected) { if (actual != expected) throw new FogException("denied"); }

    private sealed class UploadState(string session, string file)
    {
        internal string Session { get; } = session;
        internal string File { get; } = file;
        internal MemoryStream Bytes { get; } = new();
    }

    private sealed class Job(string owner, FogFileNode directory, Dictionary<string, FogFileNode> files, long now)
    {
        internal readonly string Owner = owner;
        internal readonly FogFileNode Directory = directory;
        internal readonly Dictionary<string, FogFileNode> Files = files;
        internal readonly Dictionary<string, byte[]> Inputs = new();
        internal readonly CancellationTokenSource Stop = new();
        internal UploadState? Writer;
        internal string State = "staging";
        internal string? Error;
        internal byte[] Result = [];
        internal long Cpu;
        internal long RetainedAt = now;
        internal bool CancelRequested;
        internal Task Work = Task.CompletedTask;
    }
}
