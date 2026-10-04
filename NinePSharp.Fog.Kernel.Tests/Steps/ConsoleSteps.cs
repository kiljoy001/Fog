using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "/dev holds devcons's null, zero, pid, ppid and user, as 9front binds #c there")]
public sealed class ConsoleSteps(KernelDriver driver)
{
    private readonly List<string> childRead = new();
    private long child;
    private string listed = string.Empty;
    private string? failure;
    private bool opened;

    [Given(@"^a kernel booted for the host owner ""(.*)""$")]
    public async Task GivenKernel(string user)
        => driver.Init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), user).BootAsync();

    [When(@"^the process lists ""(.*)""$")]
    public async Task WhenLists(string path)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        listed = string.Join(' ', (await driver.Init.DirReadAsync(fd)).Select(entry => entry.Name));
    }

    [When(@"^the process forks a child that reads ""(.*)"" and ""(.*)""$")]
    public async Task WhenChildReads(string first, string second)
    {
        child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            childRead.Add(await ReadAsync(process, first, 100));
            childRead.Add(await ReadAsync(process, second, 100));
        });
        await driver.Bounded(new ValueTask<Waitmsg>(driver.Init.WaitAsync()));
    }

    [When(@"^the process writes ""(.*)"" to ""(.*)""$")]
    public async Task WhenWrites(string text, string path)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OWRITE);
        try
        {
            await driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text));
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When(@"^the process opens ""(.*)"" for writing$")]
    public async Task WhenOpensForWriting(string path)
    {
        try
        {
            await driver.Init.OpenAsync(path, NinePConstants.OWRITE);
            opened = true;
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [Then(@"^it sees ""(.*)""$")]
    public void ThenSees(string names) => Assert.Equal(names, listed);

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReadingGives(string path, string text) => Assert.Equal(text, await ReadAsync(driver.Init, path, 100));

    [Then(@"^writing ""(.*)"" to ""(.*)"" writes (\d+) bytes$")]
    public async Task ThenWritingWrites(string text, string path, int count)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OWRITE);
        Assert.Equal(count, await driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text)));
    }

    [Then(@"^reading (\d+) bytes of ""(.*)"" gives (\d+) zero bytes$")]
    public async Task ThenReadingZeros(int count, string path, int zeros)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(new byte[zeros], (await driver.Init.ReadAsync(fd, count)).ToArray());
    }

    [Then("the child read its own number and then the parent's, each in twelve bytes")]
    public void ThenChildReadNumbers()
    {
        string Number(long pid) => pid.ToString(CultureInfo.InvariantCulture).PadLeft(11) + " ";
        Assert.Equal([Number(child), Number(driver.Init.Pid)], childRead);
    }

    [Then(@"^reading ""(.*)"" (\d+) bytes at a time gives (\d+) bytes and then nothing$")]
    public async Task ThenReadingInPieces(string path, int piece, int total)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        var read = new List<byte>();
        for (ReadOnlyMemory<byte> block; !(block = await driver.Init.ReadAsync(fd, piece)).IsEmpty;)
        {
            read.AddRange(block.ToArray());
        }

        Assert.Equal(driver.Init.Pid.ToString(CultureInfo.InvariantCulture).PadLeft(11) + " ", Encoding.UTF8.GetString(read.ToArray()));
        Assert.Equal(total, read.Count);
    }

    [Then(@"^a child it forks reads ""(.*)"" from ""(.*)""$")]
    public async Task ThenChildReads(string text, string path)
    {
        driver.Init.Fork(RforkFlags.Fdg, async process => childRead.Add(await ReadAsync(process, path, 100)));
        await driver.Bounded(new ValueTask<Waitmsg>(driver.Init.WaitAsync()));
        Assert.Equal([text], childRead);
    }

    [Then(@"^the write fails with ""(.*)""$")]
    public void ThenWriteFails(string message) => Assert.Equal(message, failure);

    [Then("the open succeeds")]
    public void ThenOpenSucceeds() => Assert.True(opened);

    [Then(@"^the open fails with ""(.*)""$")]
    public void ThenOpenFails(string message) => Assert.Equal(message, failure);

    private static async Task<string> ReadAsync(Process process, string path, int count)
    {
        int fd = await process.OpenAsync(path, NinePConstants.OREAD);
        string text = Encoding.UTF8.GetString((await process.ReadAsync(fd, count)).Span);
        await process.CloseAsync(fd);
        return text;
    }
}
