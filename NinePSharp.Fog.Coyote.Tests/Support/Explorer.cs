using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Coyote.Tests.Support;

// Runs a test under Coyote for a fixed number of schedules, failing with the trace that replays the first bug.
internal static class Explorer
{
    internal const uint Iterations = 500;

    internal static TestReport Explore(Func<Task> test, IReqnrollOutputHelper output)
    {
        using var engine = TestingEngine.Create(Configuration.Create().WithTestingIterations(Iterations).WithMaxSchedulingSteps(5000), test);
        engine.Run();
        TestReport report = engine.TestReport;
        if (report.UncontrolledInvocations.Count != 0)
        {
            output.WriteLine("Coyote did not control: " + string.Join(", ", report.UncontrolledInvocations.Order(StringComparer.Ordinal)));
        }

        Assert.True(report.NumOfFoundBugs == 0, string.Join(Environment.NewLine, report.BugReports) + Environment.NewLine + engine.ReproducibleTrace);
        Assert.Equal(Iterations, (uint)report.NumOfExploredFairPaths + (uint)report.NumOfExploredUnfairPaths);
        return report;
    }
}
