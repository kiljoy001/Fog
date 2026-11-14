using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace NinePSharp.Fog.Kernel.Tests.Support;

// The order tests run in: their collections, and the tests within each, by name. xunit's own order
// sorts by ids hashed from the test assembly's path, so a checkout in another directory, as the gate
// makes one for each commit, ran them in another order; a mutant then met a test that hangs before
// one that fails, or the other way round, and was a timeout in one run and killed in the next.
public sealed class NameOrder : ITestCollectionOrderer, ITestCaseOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
        => testCollections.OrderBy(c => c.DisplayName, StringComparer.Ordinal);

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
        => testCases.OrderBy(t => t.DisplayName, StringComparer.Ordinal);
}
