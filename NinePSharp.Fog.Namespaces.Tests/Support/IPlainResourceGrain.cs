using System.Collections.Concurrent;
using NinePSharp.Constants;
using NinePSharp.Namespaces.Orleans;

namespace NinePSharp.Fog.Namespaces.Tests.Support;

/// <summary>A provider without the parent capability; it is never called by the tests that use it.</summary>
public interface IPlainResourceGrain : IMountableResourceGrain
{
}
