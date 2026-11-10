using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "The environment is devenv's, each variable its group's to write whoever made it")]
public sealed class EnvironmentSteps(KernelDriver driver)
{
    private string? failure;

    private Process Init => driver.Init;

    [Given(@"^a kernel booted for the host owner ""(.*)"" whose environment holds ""(.*)"" set to ""(.*)""$")]
    public async Task GivenKernel(string owner, string name, string value)
    {
        driver.Kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(), owner, driver.Clock);
        driver.Init = await driver.Kernel.BootAsync();
        int fd = await Init.CreateAsync($"/env/{name}", NinePConstants.OWRITE, 0b110_100_100);
        await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(value));
        await Init.CloseAsync(fd);
    }

    // The child shares the parent's environment group, and becomes the user before it writes,
    // creating the file, with the mode rc asks for, as a write to a variable does.
    [When(@"^a child that becomes ""(.*)"" writes ""(.*)"" to ""(.*)""$")]
    public async Task WhenChildWrites(string user, string text, string path)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Init.Fork(RforkFlags.Fdg, async child =>
        {
            try
            {
                int u = await child.OpenAsync("/dev/user", NinePConstants.OWRITE);
                await child.WriteAsync(u, Encoding.UTF8.GetBytes(user));
                await child.CloseAsync(u);
                int fd = await child.CreateAsync(path, NinePConstants.OWRITE, 0b110_100_100);
                await child.WriteAsync(fd, Encoding.UTF8.GetBytes(text));
                await child.CloseAsync(fd);
                done.SetResult(null);
            }
            catch (SyscallException error)
            {
                done.SetResult(error.Message);
            }
        });
        failure = await done.Task.WaitAsync(KernelDriver.Bound);
    }

    [Then("the child's write succeeds")]
    public void ThenSucceeds() => Assert.Null(failure);

    [Then(@"^""(.*)"" holds ""(.*)""$")]
    public async Task ThenHolds(string path, string text)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(text, Encoding.UTF8.GetString((await Init.ReadAsync(fd, 128)).Span));
        await Init.CloseAsync(fd);
    }

    [Then(@"^""(.*)"" belongs to ""(.*)"" with the mode (\d+)$")]
    public async Task ThenBelongs(string path, string owner, string mode)
    {
        NinePSharp.Messages.Stat d = await Init.StatAsync(path);
        Assert.Equal((owner, Convert.ToUInt32(mode, 8)), (d.Uid, d.Mode & 0b111_111_111));
    }
}
