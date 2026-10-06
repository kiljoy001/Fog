using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "renumber moves a descriptor to another number in one step, as WASI's fd_renumber does")]
public sealed class RenumberSteps(KernelDriver driver)
{
    private string? failure;

    [Given(@"^the process has ""(.*)"" open as descriptor 0, read 2 bytes from it, and ""(.*)"" open as descriptor 1$")]
    public async Task GivenTwoOpen(string first, string second)
    {
        Assert.Equal(0, await driver.Init.OpenAsync(first, NinePConstants.OREAD));
        await driver.Init.ReadAsync(0, 2);
        Assert.Equal(1, await driver.Init.OpenAsync(second, NinePConstants.OREAD));
    }

    [Given(@"^the process has a pipe and ""(.*)"" open as descriptor 2$")]
    public async Task GivenPipeAndFile(string path)
    {
        var (first, second) = await driver.Init.PipeAsync();
        driver.Pipe = [first, second];
        Assert.Equal(2, await driver.Init.OpenAsync(path, NinePConstants.OREAD));
    }

    [When(@"^it renumbers descriptor (\d+) to (-?\d+)$")]
    public async Task WhenRenumbers(int fd, int target)
    {
        try
        {
            await driver.Init.RenumberAsync(fd, target);
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [Then(@"^reading descriptor (\d+) gives ""(.*)""$")]
    public async Task ThenReading(int fd, string text) => Assert.Equal(text, Encoding.UTF8.GetString((await driver.Init.ReadAsync(fd, 100)).Span));

    [Then(@"^reading descriptor (\d+) fails with ""(.*)""$")]
    public async Task ThenReadingFails(int fd, string message)
        => Assert.Equal(message, (await Assert.ThrowsAsync<SyscallException>(() => driver.Init.ReadAsync(fd, 100).AsTask())).Message);

    [Then("reading the pipe's second end gives nothing")]
    public async Task ThenPipeEmpty() => Assert.True((await driver.Bounded(driver.Init.ReadAsync(driver.Pipe[1], 100))).IsEmpty);

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);
}
