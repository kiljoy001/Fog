using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Host.Tests.Steps;

[Binding]
public sealed class BootSteps : IDisposable
{
    private const string Owner = "glenda";
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"fog-host-{Guid.NewGuid():N}");
    private FogHost? host;
    private Exception? failure;

    private string StoreFile => Path.Combine(directory, "fog.gefs");

    private Process Init => host!.Init;

    [Given("a host whose store file is in a new directory")]
    public void GivenDirectory() => Directory.CreateDirectory(directory);

    [Given("the host has booted")]
    [When("the host boots")]
    public async Task WhenBoots()
    {
        try
        {
            host = await FogHost.BootAsync(new FogHostSettings(StoreFile, Owner) { StoreBlocks = 4096 });
        }
        catch (Exception error)
        {
            failure = error;
        }
    }

    [When("the host restarts")]
    public async Task WhenRestarts()
    {
        host!.Dispose();
        host = null;
        await WhenBoots();
    }

    [Given(@"^the store file holds ""(.*)""$")]
    public Task GivenStoreFileHolds(string contents) => File.WriteAllTextAsync(StoreFile, contents);

    [When(@"^the process creates ""(.*)"" holding ""(.*)""$")]
    public async Task WhenCreates(string path, string contents)
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_100);
        await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
        await Init.CloseAsync(fd);
    }

    [Then("the store file exists")]
    public void ThenExists() => Assert.True(File.Exists(StoreFile));

    [Then(@"^the store file holds ""(.*)""$")]
    public async Task ThenStoreFileHolds(string contents) => Assert.Equal(contents, await File.ReadAllTextAsync(StoreFile));

    [Then(@"^the names in ""(.*)"" are ""(.*)""$")]
    public async Task ThenNames(string path, string names)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(names, string.Join(' ', (await Init.DirReadAsync(fd)).Select(entry => entry.Name!).Order(StringComparer.Ordinal)));
    }

    [Then(@"^""(.*)"" belongs to the host owner$")]
    public async Task ThenOwned(string path) => Assert.Equal(Owner, (await Init.StatAsync(path)).Uid);

    [Then(@"^""(.*)"" is a directory of device ""(.)""$")]
    public async Task ThenDevice(string path, char device)
    {
        var stat = await Init.StatAsync(path);
        Assert.Equal((device, true), ((char)stat.Type, (stat.Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0));
    }

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReading(string path, string contents)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(contents, Encoding.UTF8.GetString((await Init.ReadAsync(fd, 8192)).Span));
    }

    [Then(@"^the boot fails with ""(.*)""$")]
    public void ThenBootFails(string message) => Assert.Equal(message, failure?.Message);

    public void Dispose()
    {
        host?.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}
