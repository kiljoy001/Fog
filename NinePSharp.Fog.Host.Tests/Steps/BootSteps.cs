using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
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
    private IReadOnlyDictionary<string, ProgramMain> programs = new Dictionary<string, ProgramMain>();
    private TimeProvider clock = TimeProvider.System;

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
            failure = null;
            host = await FogHost.BootAsync(new FogHostSettings(StoreFile, Owner) { StoreBlocks = 4096, Programs = programs, Clock = clock });
        }
        catch (Exception error)
        {
            failure = error;
        }
    }

    [Given(@"^the host is to install a program named ""(.*)""$")]
    public void GivenProgram(string name) => programs = new Dictionary<string, ProgramMain> { [name] = (_, _) => Task.CompletedTask };

    [Given(@"^the host's clock reads (\d+) seconds since the epoch$")]
    public void GivenClock(long seconds) => clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(seconds));

    [Then(@"^the first 8 bytes of ""(.*)"" are (\d+) nanoseconds$")]
    public async Task ThenBintime(string path, long nanoseconds)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(nanoseconds, BinaryPrimitives.ReadInt64BigEndian((await Init.ReadAsync(fd, 8)).Span));
    }

    [When("the host is to install no programs")]
    public void WhenNoPrograms() => programs = new Dictionary<string, ProgramMain>();

    [When("the host restarts")]
    public async Task WhenRestarts()
    {
        host!.Dispose();
        host = null;
        await WhenBoots();
    }

    [Given(@"^the store file holds ""(.*)""$")]
    public Task GivenStoreFileHolds(string contents) => File.WriteAllTextAsync(StoreFile, contents);

    [Given(@"^the process has created ""(.*)"" holding ""(.*)""$")]
    [When(@"^the process creates ""(.*)"" holding ""(.*)""$")]
    public async Task WhenCreates(string path, string contents)
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_100);
        await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(contents));
        await Init.CloseAsync(fd);
    }

    [When(@"^the process changes the group of ""(.*)"" to ""(.*)""$")]
    public async Task WhenChangesGroup(string path, string group)
    {
        Stat none = Process.NullDir;
        try
        {
            await Init.WStatAsync(path, new Stat(0, none.Type, none.Dev, none.Qid, none.Mode, none.Atime, none.Mtime, none.Length, string.Empty, string.Empty, group, string.Empty));
        }
        catch (SyscallException error)
        {
            failure = error;
        }
    }

    [Then(@"^the change fails with ""(.*)""$")]
    public void ThenChangeFails(string message) => Assert.Equal(message, failure?.Message);

    [Then(@"^""(.*)"" has the group ""(.*)""$")]
    public async Task ThenGroup(string path, string group) => Assert.Equal(group, (await Init.StatAsync(path)).Gid);

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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
