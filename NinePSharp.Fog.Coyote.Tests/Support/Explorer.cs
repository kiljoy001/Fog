using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Coyote.Tests.Support;

// Runs a test under Coyote for a fixed number of schedules, failing with the trace that replays the first bug.
internal static class Explorer
{
    internal const uint Iterations = 500;

    // With memoryAccesses, Coyote also switches tasks at the memory accesses of assemblies rewritten to
    // check data races, as well as at their collection accesses.
    internal static TestReport Explore(Func<Task> test, IReqnrollOutputHelper output, bool memoryAccesses = false)
    {
        Configuration configuration = Configuration.Create().WithTestingIterations(Iterations).WithMaxSchedulingSteps(5000)
            .WithMemoryAccessRaceCheckingEnabled(memoryAccesses);
        using var engine = TestingEngine.Create(configuration, test);
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
