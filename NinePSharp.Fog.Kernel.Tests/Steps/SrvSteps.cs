using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "/srv posts open channels by name, as 9front's devsrv does")]
public sealed class SrvSteps(KernelDriver driver)
{
    private const uint Mode = 0b110_110_110;
    private readonly Dictionary<string, (int Fd, string Name)> leases = [];
    private readonly Dictionary<string, int> kept = [];
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
    public Task WhenPostsPipe(string path) => PostAsync(Expand(path), pipe.First);

    [When(@"^the process posts that descriptor as ""(.*)""$")]
    public Task WhenPostsHeld(string path) => PostAsync(path, held);

    [When(@"^the process opens ""(.*)"" for reading and writing$")]
    public async Task WhenOpens(string path) => opened = await Init.OpenAsync(Expand(path), NinePConstants.ORDWR);

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
    public Task WhenRenames(string path, string name) => RecordAsync(async () => await Init.WStatAsync(Expand(path), Changed(Expand(name), uint.MaxValue)));

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

    [When(@"^the process opens ""(.*)"" as the lease ""(.*)""$")]
    public async Task WhenOpensLease(string path, string lease)
    {
        int fd = await Init.OpenAsync(Expand(path), NinePConstants.OREAD);
        leases[lease] = (fd, Encoding.UTF8.GetString((await Init.ReadAsync(fd, 64)).Span));
    }

    [When(@"^the process closes the lease ""(.*)""$")]
    public Task WhenClosesLease(string lease) => Init.CloseAsync(leases[lease].Fd).AsTask();

    [When(@"^the process opens ""(.*)"" for reading and keeps it as ""(.*)""$")]
    public async Task WhenKeepsOpen(string path, string name) => kept[name] = await Init.OpenAsync(Expand(path), NinePConstants.OREAD);

    [When(@"^the process creates ""([^""]*)"" and keeps it as ""(.*)""$")]
    public async Task WhenKeepsCreated(string path, string name) => kept[name] = await Init.CreateAsync(Expand(path), NinePConstants.OWRITE, Mode);

    [When(@"^the process creates ""([^""]*)"" with ORCLOSE and keeps it as ""(.*)""$")]
    public async Task WhenKeepsCreatedOrclose(string path, string name)
        => kept[name] = await Init.CreateAsync(Expand(path), NinePConstants.OWRITE | NinePConstants.ORCLOSE, Mode);

    [When(@"^the process closes ""(.*)""$")]
    public Task WhenClosesKept(string name) => RecordAsync(() => Init.CloseAsync(kept[name]).AsTask());

    [When(@"^the process writes its pipe's first end to ""(.*)""$")]
    public Task WhenWritesKept(string name)
        => RecordAsync(() => Init.WriteAsync(kept[name], Encoding.UTF8.GetBytes(pipe.First.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsTask());

    [When(@"^the process renames what ""(.*)"" holds to ""(.*)""$")]
    public Task WhenFRenames(string name, string to) => RecordAsync(() => Init.FWStatAsync(kept[name], Changed(to, uint.MaxValue)).AsTask());

    [When(@"^the process stats what ""(.*)"" holds$")]
    public Task WhenFStats(string name) => RecordAsync(async () => await Init.FStatAsync(kept[name]));

    [When(@"^the process stats the lease ""(.*)""$")]
    public Task WhenStatsLease(string lease) => RecordAsync(async () => await Init.FStatAsync(leases[lease].Fd));

    [When(@"^the process tries to (create|remove) ""(.*)""$")]
    public Task WhenTries(string call, string path) => RecordAsync(async () =>
    {
        if (call == "create")
        {
            await Init.CreateAsync(Expand(path), NinePConstants.OWRITE, Mode);
        }
        else
        {
            await Init.RemoveAsync(Expand(path));
        }
    });

    [When(@"^the process tries to rename ""(.*)"" to ""(.*)""$")]
    public Task WhenTriesRename(string path, string name) => RecordAsync(() => Init.WStatAsync(Expand(path), Changed(Expand(name), uint.MaxValue)).AsTask());

    [When(@"^the process gives ""(.*)"" to ""(.*)""$")]
    public Task WhenChowns(string path, string user) => Init.WStatAsync(Expand(path), Changed(string.Empty, uint.MaxValue, user)).AsTask();

    [When(@"^(the process|a child that becomes ""none"") posts the pipe's first end as ""(.*)"" with the mode (\d+)$")]
    public Task WhenPostsWithMode(string who, string path, string mode) => AsAsync(who, async process =>
    {
        int post = await process.CreateAsync(Expand(path), NinePConstants.OWRITE, Convert.ToUInt32(mode, 8));
        await process.WriteAsync(post, Encoding.UTF8.GetBytes(pipe.First.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await process.CloseAsync(post);
    });

    [When(@"^(the process|a child that becomes ""none"") opens ""([^""]*)"" for reading$")]
    public Task WhenOpensAs(string who, string path) => AsAsync(who, async process => await process.OpenAsync(Expand(path), NinePConstants.OREAD));

    [When(@"^a child that becomes ""none"" creates ""([^""]*)""$")]
    public Task WhenChildCreates(string path) => AsAsync("a child", async process => await process.CreateAsync(Expand(path), NinePConstants.OWRITE, Mode));

    [When(@"^a child that becomes ""none"" renames ""(.*)"" to ""(.*)""$")]
    public Task WhenChildRenames(string path, string name) => AsAsync("a child", async process => await process.WStatAsync(Expand(path), Changed(Expand(name), uint.MaxValue)));

    [When(@"^the process binds ""(.*)"" (on|after) ""(.*)""$")]
    public async Task WhenBinds(string name, string how, string old)
    {
        if (old == "/env/p")
        {
            int fd = await Init.CreateAsync(old, NinePConstants.OWRITE, Mode);
            await Init.CloseAsync(fd);
        }

        await Init.BindAsync(Expand(name), old, how == "on" ? MountFlags.Replace : MountFlags.After);
    }

    [When(@"^the process binds ""(.*)"" on ""(.*)"", allowing creation$")]
    public Task WhenBindsCreating(string name, string old) => Init.BindAsync(Expand(name), old, MountFlags.Replace | MountFlags.Create).AsTask();

    [When(@"^the process sets the mode of ""(.*)"" to (\d+)$")]
    public Task WhenSetsMode(string path, string mode) => RecordAsync(() => Init.WStatAsync(Expand(path), Changed(string.Empty, Convert.ToUInt32(mode, 8))).AsTask());

    [When(@"^the process lists ""(.*)""$")]
    public Task WhenLists(string path) => RecordAsync(async () =>
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        await Init.DirReadAsync(fd);
    });

    [When(@"^the process stats ""(.*)""$")]
    public Task WhenStats(string path) => RecordAsync(async () => await Init.StatAsync(Expand(path)));

    [When("the process closes the board's clone")]
    public Task WhenClosesClone() => Init.CloseAsync(clone).AsTask();

    [Then(@"^the pipe's second end reads ""(.*)""$")]
    public async Task ThenReads(string text) => Assert.Equal(text, Encoding.UTF8.GetString((await Init.ReadAsync(pipe.Second, 64).AsTask().WaitAsync(KernelDriver.Bound)).Span));

    [Then(@"^the names in ""(.*)"" are ""(.*)""$")]
    public async Task ThenNames(string path, string names)
    {
        int fd = await Init.OpenAsync(Expand(path), NinePConstants.OREAD);
        Assert.Equal(Expand(names).Split(' '), (await Init.DirReadAsync(fd)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
        await Init.CloseAsync(fd);
    }

    [Then(@"^opening ""(.*)"" for (reading|writing) fails with ""(.*)""$")]
    public async Task ThenOpenFails(string path, string how, string error)
    {
        await RecordAsync(async () => await Init.OpenAsync(Expand(path), how == "reading" ? NinePConstants.OREAD : NinePConstants.OWRITE));
        Assert.Equal(Expand(error), failure);
    }

    [Then(@"^opening ""(.*)"" for reading succeeds$")]
    public async Task ThenOpenSucceeds(string path) => await Init.CloseAsync(await Init.OpenAsync(path, NinePConstants.OREAD));

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string error) => Assert.Equal(Expand(error), failure);

    [Then("the call succeeds")]
    public void ThenSucceeds() => Assert.Null(failure);

    [Then(@"^the lease ""(.*)"" is named ""(.*)""$")]
    public void ThenLeaseNamed(string lease, string name) => Assert.Equal(name, leases[lease].Name);

    [Then(@"^the lease ""(.*)"" read from offset (\d+) gives ""(.*)""$")]
    public async Task ThenLeaseFrom(string lease, int offset, string text)
        => Assert.Equal(text, Encoding.UTF8.GetString((await Init.PReadAsync(leases[lease].Fd, 64, offset)).Span));

    [Then(@"^the names in what ""(.*)"" holds are ""(.*)""$")]
    public async Task ThenKeptNames(string name, string names)
        => Assert.Equal(Expand(names).Split(' '), (await Init.DirReadAsync(kept[name])).Select(entry => entry.Name).Order(StringComparer.Ordinal));

    [Then(@"^""(.*)"" belongs to ""(.*)"" with the mode (\d+)$")]
    public async Task ThenOwned(string path, string user, string mode)
    {
        Stat stat = await Init.StatAsync(Expand(path));
        Assert.Equal((user, Convert.ToUInt32(mode, 8)), (stat.Uid, stat.Mode & 0b111_111_111));
    }

    [Then(@"^the child's call fails with ""(.*)""$")]
    public async Task ThenChildFails(string error) => Assert.Equal(error, await childCall!.WaitAsync(KernelDriver.Bound));

    [Then(@"^""(.*)"" has the mode (\d+)$")]
    public async Task ThenMode(string path, string mode) => Assert.Equal(Convert.ToUInt32(mode, 8), (await Init.StatAsync(path)).Mode & 0b111_111_111);

    [Then(@"^""(.*)"" is a directory$")]
    public async Task ThenDirectory(string path) => Assert.True(((await Init.StatAsync(Expand(path))).Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0);

    [Then(@"^the opened /srv file is the pipe's first end$")]
    public async Task ThenSameChannel()
    {
        await Init.WriteAsync(opened, "x"u8.ToArray());
        Assert.Equal("x", Encoding.UTF8.GetString((await Init.ReadAsync(pipe.Second, 64).AsTask().WaitAsync(KernelDriver.Bound)).Span));
    }

    private static Stat Changed(string name, uint mode, string user = "")
    {
        Stat none = Process.NullDir;
        return new Stat(0, none.Type, none.Dev, none.Qid, mode, none.Atime, none.Mtime, none.Length, name, user, string.Empty, string.Empty);
    }

    private string Board(string text) => text.Replace("<board>", board, StringComparison.Ordinal);

    // <board> is the board's name, <A> the board of the lease A, and <n c> n of the character c.
    private string Expand(string text)
    {
        text = Board(text);
        foreach ((string lease, (_, string name)) in leases)
        {
            text = text.Replace($"<{lease}>", name, StringComparison.Ordinal);
        }

        return System.Text.RegularExpressions.Regex.Replace(text, @"<(\d+) (.)>", match => new string(match.Groups[2].Value[0], int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    // The call made by the process or by a child that becomes none, its failure kept.
    private Task AsAsync(string who, Func<Process, Task> call)
    {
        if (who == "the process")
        {
            return RecordAsync(() => call(Init));
        }

        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Init.Fork(RforkFlags.Proc, async child =>
        {
            int user = await child.OpenAsync("/dev/user", NinePConstants.OWRITE);
            await child.WriteAsync(user, "none"u8.ToArray());
            await child.CloseAsync(user);
            try
            {
                await call(child);
                result.SetResult(null);
            }
            catch (SyscallException error)
            {
                result.SetResult(error.Message);
            }
        });
        return result.Task.WaitAsync(KernelDriver.Bound).ContinueWith(done => failure = done.Result, TaskScheduler.Default);
    }

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
