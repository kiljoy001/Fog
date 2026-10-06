using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "pread, pwrite, fstat and wstat act on files as 9front's do")]
public sealed class FileStatusSteps(KernelDriver driver)
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(50);
    private Stat? entry;
    private string? read;
    private string? failure;
    private Task<int>? write;

    [Given(@"^the process has ""(.*)"" open as descriptor 0$")]
    public async Task GivenOpen(string path) => Assert.Equal(0, await driver.Init.OpenAsync(path, NinePConstants.ORDWR));

    [When(@"^it preads (\d+) bytes at offset (-?\d+)$")]
    public async Task WhenPread(int count, long offset) => read = Encoding.UTF8.GetString((await driver.Init.PReadAsync(0, count, offset)).Span);

    [Given(@"^it pwrites ""(.*)"" at offset (-?\d+)$")]
    [When(@"^it pwrites ""(.*)"" at offset (-?\d+)$")]
    public Task WhenPwrite(string text, long offset)
        => CallAsync(async () => Assert.Equal(text.Length, await driver.Init.PWriteAsync(0, Encoding.UTF8.GetBytes(text), offset)));

    [Given(@"^""(.*)"" creates ""(.*)"" holding ""(.*)"" with the mode (\d+)$")]
    public async Task GivenCreates(string user, string path, string contents, string mode)
    {
        string caller = driver.Init.User;
        driver.Init.User = user;
        int fd = await driver.Init.CreateAsync(path, NinePConstants.OWRITE, Convert.ToUInt32(mode, 8));
        await driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
        await driver.Init.CloseAsync(fd);
        driver.Init.User = caller;
    }

    [When(@"^the process fstats descriptor (\d+)$")]
    public Task WhenFstat(int fd) => CallAsync(async () => entry = await driver.Init.FStatAsync(fd));

    [When(@"^the process stats ""(.*)""$")]
    public async Task WhenStat(string path) => entry = await driver.Init.StatAsync(path);

    [Given(@"^the process runs as ""(.*)""$")]
    public void GivenUser(string user) => driver.Init.User = user;

    [Given(@"^the process wstats ""(.*)"" with (.*)$")]
    [When(@"^the process wstats ""(.*)"" with (.*)$")]
    public Task WhenWstat(string path, string change) => CallAsync(() => driver.Init.WStatAsync(path, Change(change)).AsTask());

    [When(@"^the process fwstats descriptor (\d+) with (.*)$")]
    public Task WhenFwstat(int fd, string change) => CallAsync(() => driver.Init.FWStatAsync(fd, Change(change)).AsTask());

    [When("the process makes a pipe")]
    public async Task WhenPipe()
    {
        var (first, second) = await driver.Init.PipeAsync();
        driver.Pipe = [first, second];
    }

    [When(@"^the process fwstats the pipe's first end with (.*)$")]
    public Task WhenFwstatPipe(string change) => CallAsync(() => driver.Init.FWStatAsync(driver.Pipe[0], Change(change)).AsTask());

    [When(@"^it writes ""(.*)"" to the first end$")]
    public async Task WhenWritesFirst(string text) => await driver.Bounded(driver.Init.WriteAsync(driver.Pipe[0], Encoding.UTF8.GetBytes(text)));

    [When(@"^the process fstats the pipe's (first|second) end$")]
    public async Task WhenFstatEnd(string end) => entry = await driver.Init.FStatAsync(End(end));

    [When(@"^it starts writing (\d+) bytes to the (first|second) end$")]
    public void WhenStartWriting(int count, string end) => write = driver.Init.WriteAsync(End(end), new byte[count]).AsTask();

    [When(@"^it reads (\d+) bytes from the (first|second) end$")]
    public async Task WhenRead(int count, string end) => await driver.Bounded(driver.Init.ReadAsync(End(end), count));

    [Given(@"^the clock reads (\d+)$")]
    public void GivenClock(long seconds) => driver.Clock.Now = DateTimeOffset.FromUnixTimeSeconds(seconds);

    [Then(@"^the pread gives ""(.*)""$")]
    public void ThenPread(string text) => Assert.Equal(text, read);

    [Then(@"^reading descriptor 0 gives ""(.*)""$")]
    public async Task ThenReadingDescriptor(string text) => Assert.Equal(text, Encoding.UTF8.GetString((await driver.Init.ReadAsync(0, 100)).Span));

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReadingPath(string path, string text)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(text, Encoding.UTF8.GetString((await driver.Init.ReadAsync(fd, 100)).Span));
        await driver.Init.CloseAsync(fd);
    }

    [Then(@"^the entry is named ""(.*)"", is (\d+) bytes long and has the permissions (d?)(\d+)$")]
    public void ThenEntry(string name, ulong length, string directory, string permissions)
    {
        Assert.Equal(name, entry!.Value.Name);
        Assert.Equal(length, entry.Value.Length);
        Assert.Equal(directory.Length != 0, (entry.Value.Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0);
        Assert.Equal(Convert.ToUInt32(permissions, 8), entry.Value.Mode & 0b111_111_111);
    }

    [Then(@"^stating ""(.*)"" gives the group ""(.*)""$")]
    public async Task ThenGroup(string path, string group) => Assert.Equal(group, (await driver.Init.StatAsync(path)).Gid);

    [Then(@"^stating ""(.*)"" gives the length (\d+)$")]
    public async Task ThenLength(string path, ulong length) => Assert.Equal(length, (await driver.Init.StatAsync(path)).Length);

    [Then(@"^preading (\d+) bytes at (\d+) gives the bytes (.*)$")]
    public async Task ThenPreading(int count, long offset, string bytes)
        => Assert.Equal(bytes.Split(' ').Select(byte.Parse).ToArray(), (await driver.Init.PReadAsync(0, count, offset)).ToArray());

    [Then(@"^preading (\d+) bytes at (\d+) fails with ""(.*)""$")]
    public async Task ThenPreadingFails(int count, long offset, string message)
        => Assert.Equal(message, (await Assert.ThrowsAsync<SyscallException>(() => driver.Init.PReadAsync(0, count, offset).AsTask())).Message);

    [Then(@"^stating ""(.*)"" gives the access time (\d+) and the modification time (\d+)$")]
    public async Task ThenTimes(string path, uint atime, uint mtime)
    {
        Stat stat = await driver.Init.StatAsync(path);
        Assert.Equal((atime, mtime), (stat.Atime, stat.Mtime));
    }

    [Then(@"^stating ""(.*)"" gives the modification time (\d+)$")]
    public async Task ThenModified(string path, uint time) => Assert.Equal(time, (await driver.Init.StatAsync(path)).Mtime);

    [Then("the call succeeds")]
    public void ThenSucceeds() => Assert.Null(failure);

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);

    [Then("the write has not finished")]
    public async Task ThenWriteWaits()
    {
        await Task.Delay(Settle);
        Assert.False(write!.IsCompleted);
    }

    [Then("the write finishes")]
    public async Task ThenWriteFinishes() => Assert.Equal(32, await write!.WaitAsync(KernelDriver.Bound));

    // nulldir with one field set, as a caller of dirwstat builds it.
    private static Stat Change(string change)
    {
        Stat nul = Process.NullDir;
        if (change == "nothing")
        {
            return nul;
        }

        string[] words = change.Split(' ');
        return words[^2] switch
        {
            "name" => new Stat(0, nul.Type, nul.Dev, nul.Qid, nul.Mode, nul.Atime, nul.Mtime, nul.Length, change.Split('"')[1], nul.Uid, nul.Gid, nul.Muid),
            "group" => new Stat(0, nul.Type, nul.Dev, nul.Qid, nul.Mode, nul.Atime, nul.Mtime, nul.Length, nul.Name, nul.Uid, change.Split('"')[1], nul.Muid),
            "length" => new Stat(0, nul.Type, nul.Dev, nul.Qid, nul.Mode, nul.Atime, nul.Mtime, ulong.Parse(words[^1]), nul.Name, nul.Uid, nul.Gid, nul.Muid),
            "mode" => new Stat(0, nul.Type, nul.Dev, nul.Qid, Convert.ToUInt32(words[^1], 8), nul.Atime, nul.Mtime, nul.Length, nul.Name, nul.Uid, nul.Gid, nul.Muid),
            "time" => new Stat(0, nul.Type, nul.Dev, nul.Qid, nul.Mode, nul.Atime, uint.Parse(words[^1]), nul.Length, nul.Name, nul.Uid, nul.Gid, nul.Muid),
            _ => throw new ArgumentException(change),
        };
    }

    private int End(string end) => driver.Pipe[end == "first" ? 0 : 1];

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
