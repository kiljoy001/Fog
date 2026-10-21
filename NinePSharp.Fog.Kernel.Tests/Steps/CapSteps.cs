using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "#¤ lets a process that knows a capability become the user it names, as 9front's devcap does")]
public sealed partial class CapSteps(KernelDriver driver)
{
    private string? failure;
    private string? childUser;

    private Process Init => driver.Init;

    [Given(@"^a kernel booted for the host owner ""(.*)""$")]
    public async Task GivenKernel(string owner)
    {
        driver.Kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(), owner, driver.Clock);
        driver.Init = await driver.Kernel.BootAsync();
    }

    [Given(@"^the host owner has made the capability ""(.*)"" keyed by ""(.*)""$")]
    public Task GivenCapability(string capability, string key) => MakeAsync([capability], key);

    [Given(@"^the host owner has made the capabilities ""u0"" to ""u(\d+)"" keyed by ""(.*)""$")]
    public Task GivenCapabilities(int last, string key)
        => MakeAsync([.. Enumerable.Range(0, last + 1).Select(n => "u" + n.ToString(CultureInfo.InvariantCulture))], key);

    [When(@"^(\d+) seconds pass$")]
    public void WhenTimePasses(int seconds) => driver.Clock.Now += TimeSpan.FromSeconds(seconds);

    [When(@"^a child that becomes ""(.*)"" writes ""(.*)"" to ""(.*)""$")]
    public Task WhenChildWrites(string user, string text, string path) => ChildAsync(user, async child =>
    {
        int fd = await child.OpenAsync(path, NinePConstants.OWRITE);
        await child.WriteAsync(fd, Encoding.UTF8.GetBytes(Expand(text)));
    });

    [When(@"^a child that becomes ""(.*)"" opens ""(.*)"" for writing$")]
    public Task WhenChildOpens(string user, string path) => ChildAsync(user, async child => await child.OpenAsync(path, NinePConstants.OWRITE));

    [When(@"^a child that becomes ""(.*)"" removes ""(.*)""$")]
    public Task WhenChildRemoves(string user, string path) => ChildAsync(user, async child => await child.RemoveAsync(path));

    [When(@"^the process removes ""(.*)""$")]
    public Task WhenRemoves(string path) => RecordAsync(async () => await Init.RemoveAsync(path));

    [When(@"^the process reads ""(.*)""$")]
    public Task WhenReads(string path) => RecordAsync(async () =>
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        await Init.ReadAsync(fd, 64);
    });

    [When(@"^the process writes (\d+) bytes to ""(.*)""$")]
    public Task WhenWritesBytes(int count, string path) => RecordAsync(async () =>
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OWRITE);
        await Init.WriteAsync(fd, new byte[count]);
    });

    [When(@"^the process opens ""(.*)"" for writing, becomes ""none"" and writes a hash to it$")]
    public Task WhenHostOwnerBecomesNone(string path) => RecordAsync(async () =>
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OWRITE);
        int user = await Init.OpenAsync("/dev/user", NinePConstants.OWRITE);
        await Init.WriteAsync(user, "none"u8.ToArray());
        await Init.WriteAsync(fd, new byte[20]);
    });

    [When(@"^the process opens ""(.*)"" for writing$")]
    public Task WhenOpens(string path) => RecordAsync(async () => await Init.OpenAsync(path, NinePConstants.OWRITE));

    [Then(@"^the names in ""(.*)"" are ""(.*)""$")]
    public async Task ThenNames(string path, string names)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(names, string.Join(' ', (await Init.DirReadAsync(fd)).Select(entry => entry.Name)));
        await Init.CloseAsync(fd);
    }

    [Then(@"^the names in ""(.*)"" include ""(.*)"" and ""(.*)""$")]
    public async Task ThenNamesInclude(string path, string first, string second)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        string[] names = [.. (await Init.DirReadAsync(fd)).Select(entry => entry.Name!)];
        Assert.Contains(first, names);
        Assert.Contains(second, names);
        await Init.CloseAsync(fd);
    }

    [Then(@"^""(.*)"" belongs to ""(.*)"" with the mode (d?)(\d+)$")]
    public async Task ThenOwned(string path, string user, string directory, string mode)
    {
        var stat = await Init.StatAsync(path);
        uint expected = Convert.ToUInt32(mode, 8) | (directory.Length != 0 ? (uint)NinePConstants.FileMode9P.DMDIR : 0);
        Assert.Equal((user, user, expected), (stat.Uid, stat.Gid, stat.Mode));
    }

    [Then(@"^opening ""(.*)"" for writing fails with ""(.*)""$")]
    public async Task ThenOpenFails(string path, string error)
        => Assert.Equal(error, (await Assert.ThrowsAsync<SyscallException>(() => Init.OpenAsync(path, NinePConstants.OWRITE).AsTask())).Message);

    [Then("the child's write succeeds")]
    public void ThenSucceeds() => Assert.Null(failure);

    [Then(@"^the child's (?:write|open) fails with ""(.*)""$")]
    public void ThenChildFails(string error) => Assert.Equal(error, failure);

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string error) => Assert.Equal(error, failure);

    [Then(@"^the child's user is ""(.*)""$")]
    public void ThenChildUser(string user) => Assert.Equal(user, childUser);

    // <n c> is n of the character c, and \0 is NUL.
    private static string Expand(string text)
        => Repeat().Replace(text, match => new string(match.Groups[2].Value[0], int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))).Replace("\\0", "\0", StringComparison.Ordinal);

    [GeneratedRegex(@"<(\d+) (.)>")]
    private static partial Regex Repeat();

    // As factotum makes one: the HMAC-SHA1 of the capability, keyed by the secret, written to caphash.
    private async Task MakeAsync(IReadOnlyList<string> capabilities, string key)
    {
        int fd = await Init.OpenAsync("/dev/caphash", NinePConstants.OWRITE);
        foreach (string capability in capabilities)
        {
            await Init.WriteAsync(fd, HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(capability)));
        }

        await Init.CloseAsync(fd);
    }

    // A child that becomes none unless it is to stay the host owner, makes the call, and tells
    // what it failed with, if anything, and the user it is then.
    private async Task ChildAsync(string user, Func<Process, Task> call)
    {
        var done = new TaskCompletionSource<(string?, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Init.Fork(RforkFlags.Fdg, async child =>
        {
            if (user != child.User)
            {
                int fd = await child.OpenAsync("/dev/user", NinePConstants.OWRITE);
                await child.WriteAsync(fd, Encoding.UTF8.GetBytes(user));
                await child.CloseAsync(fd);
            }

            try
            {
                await call(child);
                done.SetResult((null, child.User));
            }
            catch (SyscallException error)
            {
                done.SetResult((error.Message, child.User));
            }
        });
        (failure, childUser) = await done.Task.WaitAsync(KernelDriver.Bound);
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
