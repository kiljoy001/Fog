using NinePSharp.Fog.Rc.Tests.Support;
using Reqnroll;

namespace NinePSharp.Fog.Rc.Tests.Steps;

[Binding]
public static class RcHooks
{
    [AfterTestRun]
    public static Task StopNineFront() => NineFrontOracle.StopAsync();
}
