using NinePSharp.Fog.Server;
using NinePSharp.Fog.Symbolics;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class LinuxMathRunnerTests
{
    [LinuxWorkerFact]
    public async Task ActualSandboxEnforcesBudgetsAndRemainsUsableAfterFailure()
    {
        var runner = new LinuxMathRunner(Environment.GetEnvironmentVariable("FOG_MATH_BUNDLE")!);
        var spec = new FogMathJob("solve", "x", 5000, 15000, 268435456, 65536);
        var success = await runner.RunAsync(spec, "x^2-4"u8.ToArray(), default);
        Assert.Null(success.Error);
        Assert.True(success.CpuMilliseconds > 0);
        Assert.Equal(new[] { "-2", "2" }, MathEngine.ResultSchema.Parse(success.Result, 65536, 4).Select(row => row["value"]));
        var exhausted = await runner.RunAsync(spec with { CpuMilliseconds = 1 }, "x^2-4"u8.ToArray(), default);
        Assert.Equal("cpu-limit", exhausted.Error);
        Assert.Empty(exhausted.Result);
        var invalid = await runner.RunAsync(spec, "sin(x)"u8.ToArray(), default);
        Assert.Equal("math-syntax", invalid.Error);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = await runner.RunAsync(spec, "x^2-4"u8.ToArray(), cancelled.Token);
        Assert.Equal("deadline", stopped.Error);
        Assert.Empty(stopped.Result);
        Assert.Null((await runner.RunAsync(spec, "x^2-4"u8.ToArray(), default)).Error);
    }

    private sealed class LinuxWorkerFactAttribute : FactAttribute
    {
        public LinuxWorkerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FOG_MATH_BUNDLE") is null)
                Skip = "Set FOG_MATH_BUNDLE to the published self-contained math directory; requires user systemd, bubblewrap and libseccomp.";
        }
    }
}
