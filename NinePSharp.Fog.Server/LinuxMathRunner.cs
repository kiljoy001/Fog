using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NinePSharp.Fog.Server;

/// <summary>Experimental direct-worker isolation using transient user services and bubblewrap.</summary>
public sealed class LinuxMathRunner : IFogMathRunner
{
    private readonly string bundle;
    private readonly string bubblewrap;
    private readonly Action<string>? diagnostic;
    private int quarantined;
    private static readonly HashSet<string> ProviderErrors = ["math-syntax", "math-size", "math-domain", "math-nonfinite", "math-unresolved", "math-result", "limit"];

    public LinuxMathRunner(string bundle, string bubblewrap = "/usr/bin/bwrap", Action<string>? diagnostic = null)
    {
        this.bundle = Path.GetFullPath(bundle);
        this.bubblewrap = Path.GetFullPath(bubblewrap);
        this.diagnostic = diagnostic;
        if (!OperatingSystem.IsLinux() || !File.Exists(Path.Combine(this.bundle, "NinePSharp.Fog.Math.dll")))
            throw new ArgumentException("A published Linux math bundle is required.");
    }

    public async Task<FogMathOutcome> RunAsync(FogMathJob job, byte[] input, CancellationToken cancellation)
    {
        if (Volatile.Read(ref quarantined) != 0) return new([], "cleanup-failed", 0);
        string unit = "fog-math-" + Guid.NewGuid().ToString("N") + ".service";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(job.DeadlineMilliseconds);
        FogMathOutcome outcome = new([], "worker-lost", 0);
        using Process process = new() { StartInfo = Launch(unit, job) };
        bool started = false;
        try
        {
            started = process.Start();
            if (!started) throw new IOException("Runner process did not start.");
            outcome = await Supervise(process, unit, job, input, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { outcome = new([], "deadline", 0); }
        catch (Exception error) { diagnostic?.Invoke(error.ToString()); outcome = new([], "worker-lost", 0); }
        finally
        {
            if (!await Cleanup(unit).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref quarantined, 1);
                outcome = new([], "cleanup-failed", outcome.CpuMilliseconds);
            }
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        return outcome;
    }

    private ProcessStartInfo Launch(string unit, FogMathJob job)
    {
        var info = new ProcessStartInfo("/usr/bin/systemd-run")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        Add(info, "--user", "--quiet", "--wait", "--pipe", "--unit=" + unit,
            "--property=Type=exec", "--property=RemainAfterExit=yes", "--property=CPUAccounting=yes", "--property=MemoryAccounting=yes",
            "--property=MemoryMax=" + FogMathJob.Decimal(job.MemoryBytes), "--property=MemorySwapMax=0",
            "--property=TasksMax=64", "--property=CPUQuota=100%", "--property=OOMPolicy=kill",
            "--property=RuntimeMaxSec=" + FogMathJob.Decimal((job.DeadlineMilliseconds + 999) / 1000),
            "--property=TimeoutStopSec=2", "--property=KillMode=control-group", "--property=SendSIGKILL=yes",
            "--property=NoNewPrivileges=yes", "--property=LimitCORE=0", "--property=LimitNOFILE=128",
            "--property=LimitFSIZE=67108864", "--property=UMask=0077",
            bubblewrap, "--unshare-all", "--die-with-parent", "--new-session", "--cap-drop", "ALL",
            "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin", "--symlink", "usr/lib", "/lib",
            "--symlink", "usr/lib64", "/lib64", "--ro-bind", bundle, "/app", "--proc", "/proc", "--dev", "/dev",
            "--size", "67108864", "--tmpfs", "/tmp", "--chdir", "/app", "--clearenv",
            "--setenv", "DOTNET_ROOT", "/usr/lib/dotnet", "--setenv", "DOTNET_EnableDiagnostics", "0",
            "--setenv", "DOTNET_PROCESSOR_COUNT", "1", "--setenv", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1",
            "--setenv", "DOTNET_GCHeapHardLimit", (job.MemoryBytes / 2).ToString("X", CultureInfo.InvariantCulture),
            "--", "/app/NinePSharp.Fog.Math");
        return info;
    }

    private async Task<FogMathOutcome> Supervise(Process process, string unit, FogMathJob job, byte[] input, CancellationToken cancellation)
    {
        Task<byte[]> output = ReadBounded(process.StandardOutput.BaseStream, job.OutputBytes);
        Task<byte[]> errors = ReadBounded(process.StandardError.BaseStream, 4096);
        Task exited = process.WaitForExitAsync(CancellationToken.None);
        string group;
        try { group = await Cgroup(unit, exited, cancellation).ConfigureAwait(false); }
        catch
        {
            if (errors.IsCompletedSuccessfully) diagnostic?.Invoke(System.Text.Encoding.UTF8.GetString(errors.Result));
            throw;
        }
        string cpuFile = group + "/cpu.stat";
        _ = CpuUsage(cpuFile); // Fail closed before accepting source if accounting is missing.
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new { Source = input, job.Operation, job.Variable, job.OutputBytes });
        Task sending = Send(process, request, cancellation);
        (string? failure, long used) = await Monitor(group, job.CpuMilliseconds, output, errors, sending, exited, cancellation).ConfigureAwait(false);
        return await Complete(unit, job, output, errors, sending, exited, failure, used).ConfigureAwait(false);
    }

    private static async Task<(string?, long)> Monitor(string group, int cpuMilliseconds, Task output, Task errors, Task sending, Task exited, CancellationToken cancellation)
    {
        string? failure = null;
        long used = 0;
        while (!exited.IsCompleted)
        {
            if (cancellation.IsCancellationRequested) { failure = "deadline"; break; }
            if (output.IsFaulted || errors.IsFaulted) { failure = "output-limit"; break; }
            if (sending.IsFaulted) { failure = "worker-lost"; break; }
            if (File.Exists(group + "/cgroup.events") && File.ReadAllText(group + "/cgroup.events").Contains("populated 0\n", StringComparison.Ordinal)) break;
            try { used = CpuUsage(group + "/cpu.stat"); }
            catch (IOException) when (exited.IsCompleted || !Directory.Exists(group)) { break; }
            if (used >= cpuMilliseconds * 1000L) { failure = "cpu-limit"; break; }
            await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
        }
        return (failure, used);
    }

    private async Task<FogMathOutcome> Complete(string unit, FogMathJob job, Task<byte[]> output, Task<byte[]> errors, Task sending, Task exited, string? failure, long used)
    {
        if (failure is not null) await Command("stop", unit).ConfigureAwait(false);
        string final = await Command("show", unit, "--property=CPUUsageNSec", "--value").ConfigureAwait(false);
        if (!long.TryParse(final.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long nanos) || nanos < 0)
            return new([], failure ?? "worker-lost", (used + 999) / 1000);
        used = System.Math.Max(used, nanos / 1000);
        if (used >= job.CpuMilliseconds * 1000L) failure ??= "cpu-limit";
        if (failure is not null) return new([], failure, (used + 999) / 1000);
        string exitStatus = (await Command("show", unit, "--property=ExecMainStatus", "--value").ConfigureAwait(false)).Trim();
        string serviceResult = (await Command("show", unit, "--property=Result", "--value").ConfigureAwait(false)).Trim();
        await Command("stop", unit).ConfigureAwait(false);
        await exited.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        await sending.ConfigureAwait(false);
        byte[] result;
        byte[] stderr;
        try { result = await output.ConfigureAwait(false); stderr = await errors.ConfigureAwait(false); }
        catch (FogException) { return new([], "output-limit", (used + 999) / 1000); }
        if (exitStatus == "0") return new(result, null, (used + 999) / 1000);
        string code = System.Text.Encoding.UTF8.GetString(stderr).Trim();
        diagnostic?.Invoke($"runner exit={exitStatus} service={serviceResult} stderr={code}");
        failure = serviceResult == "oom-kill" ? "memory-limit" : serviceResult == "timeout" ? "deadline" :
            ProviderErrors.Contains(code) ? code == "limit" ? "output-limit" : code : "worker-lost";
        return new([], failure, (used + 999) / 1000);
    }

    private static async Task Send(Process process, byte[] request, CancellationToken cancellation)
    {
        await process.StandardInput.BaseStream.WriteAsync(request, cancellation).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static async Task<string> Cgroup(string unit, Task exited, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 100 && !exited.IsCompleted; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = (await Command("show", unit, "--property=ControlGroup", "--value").ConfigureAwait(false)).Trim();
            if (path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal)) return "/sys/fs/cgroup" + path;
            await Task.Delay(10, cancellation).ConfigureAwait(false);
        }
        throw new FogException("worker-lost");
    }

    private static long CpuUsage(string path)
    {
        string line = File.ReadLines(path).First(line => line.StartsWith("usage_usec ", StringComparison.Ordinal));
        return long.Parse(line.AsSpan(11), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> Cleanup(string unit)
    {
        try
        {
            await Command("stop", unit).ConfigureAwait(false);
            string state = await Command("show", unit, "--property=ActiveState", "--property=MainPID", "--property=ControlGroup").ConfigureAwait(false);
            bool clean = state.Contains("MainPID=0\n", StringComparison.Ordinal) &&
                (state.Contains("ActiveState=inactive\n", StringComparison.Ordinal) || state.Contains("ActiveState=failed\n", StringComparison.Ordinal));
            string group = state.Split('\n').FirstOrDefault(line => line.StartsWith("ControlGroup=", StringComparison.Ordinal))?[13..] ?? "";
            if (group.Length != 0 && Directory.Exists("/sys/fs/cgroup" + group))
                clean &= File.ReadAllText("/sys/fs/cgroup" + group + "/cgroup.events").Contains("populated 0\n", StringComparison.Ordinal);
            await Command("reset-failed", unit).ConfigureAwait(false);
            return clean;
        }
        catch (Exception) { return false; }
    }

    private static async Task<string> Command(params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/bin/systemctl")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        } };
        Add(process.StartInfo, "--user");
        Add(process.StartInfo, arguments);
        process.Start();
        Task<byte[]> output = ReadBounded(process.StandardOutput.BaseStream, 16384);
        Task<byte[]> errors = ReadBounded(process.StandardError.BaseStream, 16384);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); throw; }
        await errors.ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(await output.ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadBounded(Stream stream, int maximum)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk).ConfigureAwait(false)) != 0)
        {
            if (count > maximum - buffer.Length) throw new FogException("output-limit");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static void Add(ProcessStartInfo info, params string[] arguments)
    {
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
    }
}
