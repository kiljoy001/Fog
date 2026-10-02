using System.Collections.Concurrent;
using NinePSharp.Constants;
using NinePSharp.Namespaces.Orleans;

namespace NinePSharp.Fog.Namespaces.Tests.Support;

/// <summary>An application grain for tests: one root directory of seeded files, owned by "app".</summary>
public interface ITestApplicationGrain : IAncestryResourceGrain
{
    Task SeedAsync(string name, string contents);

    Task<int> GetWritesAsync();
}
