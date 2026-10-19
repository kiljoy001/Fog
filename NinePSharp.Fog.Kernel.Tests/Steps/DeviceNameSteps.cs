using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "A name starting with # begins at the device it names, as 9front's namec does")]
public sealed class DeviceNameSteps(KernelDriver driver)
{
    private string? failure;

    private Process Init => driver.Init;

    [Given(@"^a kernel booted for the host owner ""(.*)""$")]
    public async Task GivenKernel(string user)
        => driver.Init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), user, driver.Clock).BootAsync();

    [Given(@"^""(.*)"" holds ""(.*)""$")]
    public async Task GivenHolds(string path, string text)
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_100);
        await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text));
        await Init.CloseAsync(fd);
    }

    [Given(@"^the process has ""(.*)"" open$")]
    public Task GivenOpen(string path) => Init.OpenAsync(path, NinePConstants.OREAD).AsTask();

    [Given("the process has forked its namespace with RFNOMNT")]
    public Task GivenNoMount() => Init.RforkAsync(RforkFlags.Nameg | RforkFlags.Nomnt).AsTask();

    [When(@"^the process binds ""(.*)"" on ""(.*)""$")]
    public Task WhenBinds(string name, string old) => Init.BindAsync(name, old, MountFlags.Replace).AsTask();

    [When(@"^the process stats ""(.*)""$")]
    public async Task WhenStats(string path)
    {
        try
        {
            await Init.StatAsync(path);
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [Then(@"^the names in ""(.*)"" include ""(.*)""$")]
    public async Task ThenNamesInclude(string path, string name)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Contains(name, (await Init.DirReadAsync(fd)).Select(entry => entry.Name));
        await Init.CloseAsync(fd);
    }

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReads(string path, string text)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(text, Encoding.UTF8.GetString((await Init.ReadAsync(fd, 256)).Span));
        await Init.CloseAsync(fd);
    }

    [Then(@"^reading ""/lib/namespace"" gives the namespace file the kernel writes$")]
    public async Task ThenNamespaceFile()
    {
        int fd = await Init.OpenAsync("/lib/namespace", NinePConstants.OREAD);
        Assert.Equal(FogKernel.NamespaceFile, Encoding.UTF8.GetString((await Init.ReadAsync(fd, 8192)).Span));
        await Init.CloseAsync(fd);
    }

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);
}
