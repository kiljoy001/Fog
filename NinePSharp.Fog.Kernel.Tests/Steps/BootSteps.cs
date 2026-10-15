using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "The kernel boots its root as 9front's bootrc and /lib/namespace build it")]
public sealed class BootSteps(KernelDriver driver)
{
    private const string Owner = "glenda";
    private RamFs store = null!;
    private string? failure;

    [Given("a kernel booted on a store")]
    public async Task GivenBooted()
    {
        store = new RamFs("ram", "#R", Owner, clock: driver.Clock);
        await BootAsync(new Dictionary<string, ProgramMain>());
    }

    [Given(@"^the store's ""(.*)"" holds ""(.*)""$")]
    public Task GivenStoreHolds(string path, string contents) => CreateAsync("/root" + path, contents);

    [When(@"^a kernel with the program ""(.*)"" boots on the store$")]
    public Task WhenBootsWith(string name) => BootAsync(new Dictionary<string, ProgramMain> { [name] = (_, _) => Task.CompletedTask });

    [When(@"^the process creates ""(.*)"" holding ""(.*)""$")]
    public async Task WhenCreates(string path, string contents)
    {
        try
        {
            await CreateAsync(path, contents);
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When(@"^the process renames ""(.*)"" to ""(.*)""$")]
    public async Task WhenRenames(string path, string name)
    {
        Stat none = Process.NullDir;
        try
        {
            await driver.Init.WStatAsync(path, new Stat(0, none.Type, none.Dev, none.Qid, none.Mode, none.Atime, none.Mtime, none.Length, name, string.Empty, string.Empty, string.Empty));
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [Then(@"^the names in ""(.*)"" are ""(.*)""$")]
    public async Task ThenNames(string path, string names)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        IEnumerable<string> listed = (await driver.Init.DirReadAsync(fd)).Select(entry => entry.Name!).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(names, string.Join(' ', listed));
    }

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReading(string path, string contents) => Assert.Equal(contents, await ReadAsync(path));

    [Then(@"^reading ""(.*)"" gives the managed program ""(.*)""$")]
    public async Task ThenManaged(string path, string name) => Assert.Equal($"\0fog {name}\n", await ReadAsync(path));

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);

    [Then(@"^""(.*)"" is a (file|directory) of device ""(.)""$")]
    public async Task ThenDevice(string path, string kind, char device)
    {
        Stat stat = await driver.Init.StatAsync(path);
        Assert.Equal((device, kind == "directory"), ((char)stat.Type, (stat.Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0));
    }

    private async Task BootAsync(IReadOnlyDictionary<string, ProgramMain> programs)
    {
        driver.Kernel = new FogKernel(programs, store, store.Root, Owner);
        driver.Init = await driver.Kernel.BootAsync();
    }

    private async Task CreateAsync(string path, string contents)
    {
        int fd = await driver.Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_100);
        await driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
        await driver.Init.CloseAsync(fd);
    }

    private async Task<string> ReadAsync(string path)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        string contents = Encoding.UTF8.GetString((await driver.Init.ReadAsync(fd, 8192)).Span);
        await driver.Init.CloseAsync(fd);
        return contents;
    }
}
