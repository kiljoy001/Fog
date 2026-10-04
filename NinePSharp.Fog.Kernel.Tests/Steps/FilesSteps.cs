using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "stat, seek and bind act on files and the namespace as 9front's do")]
public sealed class FilesSteps(KernelDriver driver)
{
    private Stat? entry;
    private long position;
    private string? failure;

    [Given(@"^""(.*)"" holds ""(.*)"" in a new directory$")]
    public async Task GivenHoldsInDirectory(string path, string contents)
    {
        string directory = path[..path.LastIndexOf('/')];
        await driver.Init.CloseAsync(await driver.Init.CreateAsync(directory, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_111));
        await WriteAsync(driver.Init, path, contents);
    }

    [Given(@"^the process has ""(.*)"" open as descriptor 0$")]
    public async Task GivenOpen(string path) => Assert.Equal(0, await driver.Init.OpenAsync(path, NinePConstants.ORDWR));

    [Given(@"^the process has ""(.*)"" open ORCLOSE as descriptor 0$")]
    public async Task GivenOpenOrclose(string path)
        => Assert.Equal(0, await driver.Init.OpenAsync(path, NinePConstants.OREAD | NinePConstants.ORCLOSE));

    [When(@"^the process dups descriptor (\d+)$")]
    public async Task WhenDups(int fd) => await driver.Init.DupAsync(fd, -1);

    [When(@"^the process closes descriptor (\d+)$")]
    public async Task WhenCloses(int fd) => await driver.Init.CloseAsync(fd);

    [When(@"^the process stats ""(.*)""$")]
    public Task WhenStats(string path) => CallAsync(async () => entry = await driver.Init.StatAsync(path));

    [When(@"^it seeks descriptor 0 to (-?\d+) from (the start|where it is|the end)$")]
    public async Task WhenSeeks(long offset, string whence)
        => position = await driver.Init.SeekAsync(0, offset, whence switch { "the start" => 0, "where it is" => 1, _ => 2 });

    [When(@"^the process binds ""(.*)"" onto ""([^""]*)""$")]
    public Task WhenBinds(string name, string old) => CallAsync(() => driver.Init.BindAsync(name, old, MountFlags.Replace).AsTask());

    [When(@"^the process binds ""(.*)"" onto ""([^""]*)"" with (-\w+)$")]
    public Task WhenBindsWith(string name, string old, string flag)
        => CallAsync(() => driver.Init.BindAsync(name, old, flag == "-b" ? MountFlags.Before : flag == "-a" ? MountFlags.After : MountFlags.Before | MountFlags.Create).AsTask());

    [When(@"^the process creates ""(.*)"" holding ""(.*)""$")]
    public Task WhenCreates(string path, string contents) => CallAsync(() => WriteAsync(driver.Init, path, contents));

    [Given(@"^the process has (\d+) descriptors open$")]
    public async Task GivenDescriptors(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await driver.Init.OpenAsync("/dev/null", NinePConstants.OREAD);
        }
    }

    [When(@"^the process (opens|creates) ""([^""]*)""$")]
    public Task WhenOpensOrCreates(string call, string path) => CallAsync(async () =>
    {
        if (call == "opens")
        {
            await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        }
        else
        {
            await driver.Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        }
    });

    [When("the process makes a pipe")]
    public Task WhenMakesPipe() => CallAsync(async () => await driver.Init.PipeAsync());

    [When(@"^a child (sharing all of them calls rfork with|is forked with) (RF\w+) and then (.*)$")]
    public async Task WhenChild(string given, string flag, string change)
    {
        RforkFlags flags = Enum.Parse<RforkFlags>(flag[2..], ignoreCase: true);
        bool rfork = given.StartsWith("sharing", StringComparison.Ordinal);
        driver.Init.Fork(rfork ? 0 : flags, async child =>
        {
            if (rfork)
            {
                await child.RforkAsync(flags);
            }

            await ChangeAsync(child, change);
            child.Exits(null);
        });
        Assert.Equal(string.Empty, (await driver.Bounded(new ValueTask<Waitmsg>(driver.Init.WaitAsync()))).Message);
    }

    [When(@"^the process calls rfork with (.*)$")]
    public Task WhenRforks(string flags)
        => CallAsync(() => driver.Init.RforkAsync(flags.Split(" and ").Select(flag => Enum.Parse<RforkFlags>(flag[2..], ignoreCase: true)).Aggregate((a, b) => a | b)).AsTask());

    [Then(@"^the entry is named ""(.*)"", is (\d+) bytes long and has the permissions (d?)(\d+)$")]
    public void ThenEntry(string name, ulong length, string directory, string permissions)
    {
        Assert.Equal(name, entry!.Value.Name);
        Assert.Equal(length, entry.Value.Length);
        Assert.Equal(directory.Length != 0, (entry.Value.Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0);
        Assert.Equal(Convert.ToUInt32(permissions, 8), entry.Value.Mode & 0b111_111_111);
    }

    [Then("no pipe is left")]
    public void ThenNoPipe() => Assert.Equal(0, driver.Kernel.Pipes.Count);

    [Then(@"^the process can still open ""(.*)""$")]
    public async Task ThenCanOpen(string path) => Assert.Equal(4999, await driver.Init.OpenAsync(path, NinePConstants.OREAD));

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);

    [Then(@"^the seek returns (\d+)$")]
    public void ThenSeekReturns(long expected) => Assert.Equal(expected, position);

    [Then(@"^reading descriptor 0 gives ""(.*)""$")]
    public async Task ThenReadingGives(string text) => Assert.Equal(text, Encoding.UTF8.GetString((await driver.Init.ReadAsync(0, 100)).Span));

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReadingPathGives(string path, string text) => Assert.Equal(text, await ReadAsync(driver.Init, path));

    [Then(@"^listing ""(.*)"" gives ""(.*)""$")]
    public async Task ThenListing(string path, string names)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(names, string.Join(' ', (await driver.Init.DirReadAsync(fd)).Select(e => e.Name)));
    }

    [Then(@"^the parent still has descriptor 0$")]
    public async Task ThenStillHasZero() => await driver.Init.ReadAsync(0, 1);

    [Then(@"^the parent reads ""(.*)"" from ""(.*)""$")]
    public async Task ThenParentReads(string text, string path) => Assert.Equal(text, await ReadAsync(driver.Init, path));

    private static async Task ChangeAsync(Process child, string change)
    {
        string[] quoted = change.Split('"');
        switch (quoted[0])
        {
            case "closes descriptor 0":
                await child.CloseAsync(0);
                break;
            case "finds descriptor 0 closed":
                await Assert.ThrowsAsync<SyscallException>(() => child.ReadAsync(0, 1).AsTask());
                break;
            case "writes ":
                await WriteAsync(child, quoted[3], quoted[1]);
                break;
            case "binds ":
                await child.BindAsync(quoted[1], quoted[3], MountFlags.Replace);
                break;
            default:
                await Assert.ThrowsAsync<SyscallException>(() => child.OpenAsync(quoted[1], NinePConstants.OREAD).AsTask());
                break;
        }
    }

    private static async Task WriteAsync(Process process, string path, string contents)
    {
        int fd = await process.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await process.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
        await process.CloseAsync(fd);
    }

    private static async Task<string> ReadAsync(Process process, string path)
    {
        int fd = await process.OpenAsync(path, NinePConstants.OREAD);
        string text = Encoding.UTF8.GetString((await process.ReadAsync(fd, 100)).Span);
        await process.CloseAsync(fd);
        return text;
    }

    private async Task CallAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }
}
