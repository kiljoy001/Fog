using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "/srv posts open channels by name, as 9front's devsrv does")]
public sealed class SrvSteps(KernelDriver driver)
{
    private const uint Mode = 0b110_110_110;
    private (int First, int Second) pipe;
    private int held = -1;
    private int opened = -1;
    private int clone = -1;
    private string board = string.Empty;
    private string? failure;
    private Task<string?>? childCall;

    private Process Init => driver.Init;

    [Given(@"^a kernel booted for the host owner ""(.*)""$")]
    public async Task GivenKernel(string owner)
    {
        driver.Kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(), owner, driver.Clock);
        driver.Init = await driver.Kernel.BootAsync();
    }

    [Given("the process holds a pipe")]
    public async Task GivenPipe() => pipe = await Init.PipeAsync();

    [Given(@"^the process has ""(.*)"" open for reading$")]
    public async Task GivenOpen(string path) => held = await Init.OpenAsync(path, NinePConstants.OREAD);

    [When(@"^the process posts the pipe's first end as ""(.*)""$")]
    public Task WhenPostsPipe(string path) => PostAsync(Board(path), pipe.First);

    [When(@"^the process posts that descriptor as ""(.*)""$")]
    public Task WhenPostsHeld(string path) => PostAsync(path, held);

    [When(@"^the process opens ""(.*)"" for reading and writing$")]
    public async Task WhenOpens(string path) => opened = await Init.OpenAsync(Board(path), NinePConstants.ORDWR);

    [When("the process closes the pipe's first end")]
    public Task WhenClosesFirst() => Init.CloseAsync(pipe.First).AsTask();

    [When(@"^the process writes ""(.*)"" to the opened /srv file$")]
    public Task WhenWrites(string text) => Init.WriteAsync(opened, Encoding.UTF8.GetBytes(text)).AsTask();

    [When(@"^the process creates ""(.*)"" for (reading|writing)$")]
    public Task WhenCreates(string path, string how) => RecordAsync(async () => await Init.CreateAsync(path, how == "reading" ? NinePConstants.OREAD : NinePConstants.OWRITE, Mode));

    [When(@"^the process writes ""(.*)"" to a new ""(.*)""$")]
    public Task WhenWritesNew(string text, string path) => RecordAsync(async () =>
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE, Mode);
        await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text));
    });

    [When(@"^the process writes its pipe's first end to a new ""(.*)"" twice$")]
    public Task WhenWritesTwice(string path) => RecordAsync(async () =>
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE, Mode);
        byte[] number = Encoding.UTF8.GetBytes(pipe.First.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Init.WriteAsync(fd, number);
        await Init.WriteAsync(fd, number);
    });

    [When(@"^the process opens ""(.*)"" with (OTRUNC|ORCLOSE)$")]
    public Task WhenOpensWith(string path, string flag) => RecordAsync(async () => await Init.OpenAsync(path, NinePConstants.OREAD | (flag == "OTRUNC" ? NinePConstants.OTRUNC : NinePConstants.ORCLOSE)));

    [When(@"^the process opens a ""(.*)"" it created but never posted$")]
    public Task WhenOpensEmpty(string path) => RecordAsync(async () =>
    {
        await Init.CreateAsync(path, NinePConstants.OWRITE, Mode);
        await Init.OpenAsync(path, NinePConstants.OREAD);
    });

    [When(@"^a child that becomes ""none"" removes ""(.*)""$")]
    public void WhenChildRemoves(string path)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        childCall = result.Task;
        Init.Fork(RforkFlags.Proc, async child =>
        {
            int user = await child.OpenAsync("/dev/user", NinePConstants.OWRITE);
            await child.WriteAsync(user, "none"u8.ToArray());
            await child.CloseAsync(user);
            try
            {
                await child.RemoveAsync(path);
                result.SetResult(null);
            }
            catch (SyscallException error)
            {
                result.SetResult(error.Message);
            }
        });
    }

    [When(@"^the process removes ""(.*)""$")]
    public Task WhenRemoves(string path) => Init.RemoveAsync(path).AsTask();

    [When(@"^the process renames ""(.*)"" to ""(.*)"" and sets its mode to (\d+)$")]
    public Task WhenRenamesAndModes(string path, string name, string mode) => Init.WStatAsync(path, Changed(name, Convert.ToUInt32(mode, 8))).AsTask();

    [When(@"^the process renames ""(.*)"" to ""(.*)""$")]
    public Task WhenRenames(string path, string name) => RecordAsync(async () => await Init.WStatAsync(path, Changed(name, uint.MaxValue)));

    [When(@"^the process creates ""(.*)"" with ORCLOSE and posts the pipe's first end through it$")]
    public async Task WhenCreatesOrclose(string path)
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE | NinePConstants.ORCLOSE, Mode);
        await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(pipe.First.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await Init.CloseAsync(fd);
    }

    [When(@"^the process opens ""/srv/clone"" and reads the board's name$")]
    public async Task WhenOpensClone()
    {
        clone = await Init.OpenAsync("/srv/clone", NinePConstants.OREAD);
        board = Encoding.UTF8.GetString((await Init.ReadAsync(clone, 64)).Span);
    }

    [When("the process closes the board's clone")]
    public Task WhenClosesClone() => Init.CloseAsync(clone).AsTask();

    [Then(@"^the pipe's second end reads ""(.*)""$")]
    public async Task ThenReads(string text) => Assert.Equal(text, Encoding.UTF8.GetString((await Init.ReadAsync(pipe.Second, 64).AsTask().WaitAsync(KernelDriver.Bound)).Span));

    [Then(@"^the names in ""(.*)"" are ""(.*)""$")]
    public async Task ThenNames(string path, string names)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(names.Split(' '), (await Init.DirReadAsync(fd)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
        await Init.CloseAsync(fd);
    }

    [Then(@"^opening ""(.*)"" for (reading|writing) fails with ""(.*)""$")]
    public async Task ThenOpenFails(string path, string how, string error)
    {
        await RecordAsync(async () => await Init.OpenAsync(Board(path), how == "reading" ? NinePConstants.OREAD : NinePConstants.OWRITE));
        Assert.Equal(Board(error), failure);
    }

    [Then(@"^opening ""(.*)"" for reading succeeds$")]
    public async Task ThenOpenSucceeds(string path) => await Init.CloseAsync(await Init.OpenAsync(path, NinePConstants.OREAD));

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string error) => Assert.Equal(error, failure);

    [Then(@"^the child's call fails with ""(.*)""$")]
    public async Task ThenChildFails(string error) => Assert.Equal(error, await childCall!.WaitAsync(KernelDriver.Bound));

    [Then(@"^""(.*)"" has the mode (\d+)$")]
    public async Task ThenMode(string path, string mode) => Assert.Equal(Convert.ToUInt32(mode, 8), (await Init.StatAsync(path)).Mode & 0b111_111_111);

    [Then(@"^""(.*)"" is a directory$")]
    public async Task ThenDirectory(string path) => Assert.True(((await Init.StatAsync(Board(path))).Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0);

    [Then(@"^the opened /srv file is the pipe's first end$")]
    public async Task ThenSameChannel()
    {
        await Init.WriteAsync(opened, "x"u8.ToArray());
        Assert.Equal("x", Encoding.UTF8.GetString((await Init.ReadAsync(pipe.Second, 64).AsTask().WaitAsync(KernelDriver.Bound)).Span));
    }

    private static Stat Changed(string name, uint mode)
    {
        Stat none = Process.NullDir;
        return new Stat(0, none.Type, none.Dev, none.Qid, mode, none.Atime, none.Mtime, none.Length, name, string.Empty, string.Empty, string.Empty);
    }

    private string Board(string text) => text.Replace("<board>", board, StringComparison.Ordinal);

    private async Task PostAsync(string path, int fd)
    {
        int post = await Init.CreateAsync(path, NinePConstants.OWRITE, Mode);
        await Init.WriteAsync(post, Encoding.UTF8.GetBytes(fd.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await Init.CloseAsync(post);
    }

    private async Task RecordAsync(Func<Task> call)
    {
        try
        {
            await call();
            failure = null;
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }
}
