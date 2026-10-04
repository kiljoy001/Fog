using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "/fd names a process's open descriptors as 9front's devdup does")]
public sealed class DescriptorSteps(KernelDriver driver)
{
    private int opened = -1;
    private string? failure;
    private string listed = string.Empty;
    private string permissions = string.Empty;

    [Given(@"^a process with ""(.*)"" open for (.*) as descriptor 0$")]
    public async Task GivenOpen(string path, string mode) => Assert.Equal(0, await driver.Init.OpenAsync(path, Mode(mode)));

    [Given(@"^it has read (\d+) bytes from descriptor 0$")]
    public async Task GivenRead(int count) => Assert.Equal(count, (await driver.Bounded(driver.Init.ReadAsync(0, count))).Length);

    [When(@"^it opens ""(.*)"" for (.*)$")]
    public async Task WhenOpens(string path, string mode)
    {
        try
        {
            opened = await driver.Init.OpenAsync(path, Mode(mode));
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When(@"^it creates ""(.*)"" for (.*)$")]
    public async Task WhenCreates(string path, string mode)
    {
        try
        {
            opened = await driver.Init.CreateAsync(path, Mode(mode), 0b110_110_110);
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When(@"^it writes ""(.*)"" to descriptor (\d+)$")]
    public async Task WhenWrites(string text, int fd) => await driver.Bounded(driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text)));

    [When(@"^it (reads|writes to) descriptor (\d+)$")]
    public async Task WhenUses(string call, int fd)
    {
        try
        {
            _ = call == "reads"
                ? (await driver.Bounded(driver.Init.ReadAsync(fd, 8192))).Length
                : await driver.Bounded(driver.Init.WriteAsync(fd, "x"u8.ToArray()));
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When(@"^it lists ""(.*)""$")]
    public async Task WhenLists(string path)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        IReadOnlyList<NinePSharp.Messages.Stat> entries = await driver.Init.DirReadAsync(fd);
        listed = string.Join(' ', entries.Select(entry => entry.Name));
        permissions = string.Join(' ', entries.Select(entry => Convert.ToString(entry.Mode, 8).PadLeft(4, '0')));
        await driver.Init.CloseAsync(fd);
    }

    [Then(@"^the new descriptor is (\d+)$")]
    public void ThenNewDescriptor(int fd) => Assert.Equal(fd, opened);

    [Then(@"^reading descriptor (\d+) gives ""(.*)""$")]
    public async Task ThenReadingGives(int fd, string text)
        => Assert.Equal(text, Encoding.UTF8.GetString((await driver.Bounded(driver.Init.ReadAsync(fd, 8192))).Span));

    [Then(@"^reading the new descriptor gives ""(.*)""$")]
    public Task ThenReadingNewGives(string text) => ThenReadingGives(opened, text);

    [Then(@"^it sees ""(.*)""$")]
    public void ThenSees(string names) => Assert.Equal(names, listed);

    [Then(@"^it sees the permissions ""(.*)""$")]
    public void ThenSeesPermissions(string expected) => Assert.Equal(expected, permissions);

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenCallFails(string message) => Assert.Equal(message, failure);

    [Then(@"^the open fails with ""(.*)""$")]
    public void ThenOpenFails(string message) => Assert.Equal(message, failure);

    private static int Mode(string mode) => mode switch
    {
        "reading" => NinePConstants.OREAD,
        "writing" => NinePConstants.OWRITE,
        "reading and writing" => NinePConstants.ORDWR,
        "execution" => NinePConstants.OEXEC,
        "writing, truncating" => NinePConstants.OWRITE | NinePConstants.OTRUNC,
        "reading, removed on close" => NinePConstants.OREAD | NinePConstants.ORCLOSE,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
