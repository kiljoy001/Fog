using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
public sealed class KernelSteps(KernelDriver driver)
{
    [Given("a booted kernel")]
    public async Task GivenABootedKernel()
    {
        driver.Kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>());
        driver.Init = await driver.Kernel.BootAsync();
    }

    [Given("a process with a pipe")]
    public async Task GivenAPipe()
    {
        var (first, second) = await driver.Init.PipeAsync();
        driver.Pipe = [first, second];
    }

    [Given(@"^""(.*)"" holds ""(.*)""$")]
    public async Task GivenHolds(string path, string contents)
    {
        int fd = await driver.Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
        await driver.Init.CloseAsync(fd);
    }
}
