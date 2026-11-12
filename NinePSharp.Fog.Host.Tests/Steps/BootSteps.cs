using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Host.Tests.Steps;

[Binding]
public sealed class BootSteps : IDisposable
{
    private const string Owner = "glenda";
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"fog-host-{Guid.NewGuid():N}");
    private readonly StringWriter log = new();
    private readonly TypingConsole console = new();
    private readonly HostNetwork network = new();
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
    private FogHost? host;
    private Exception? failure;
    private IReadOnlyDictionary<string, ProgramMain> programs = new Dictionary<string, ProgramMain>();
    private TimeProvider clock = TimeProvider.System;
    private IReadOnlyList<string> boot = [];
    private byte[] nvram = [];
    private string? printed;

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
            host = await FogHost.BootAsync(new FogHostSettings(StoreFile, Owner)
            {
                StoreBlocks = 4096,
                Programs = programs,
                Clock = clock,
                Files = files,
                Boot = boot,
                Host = network,
                Console = console,
                Nvram = new HostNvram(nvram),
                Log = log,
            });
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
        Assert.Equal(Unescape(contents), Encoding.UTF8.GetString((await Init.ReadAsync(fd, 8192)).Span));
    }

    [Given(@"^""(.*)"" holds ""([^""]*)""$")]
    public Task GivenHolds(string path, string contents) => WriteAsync(path, Encoding.UTF8.GetBytes(Unescape(contents)));

    [Given(@"^""(.*)"" holds (\d+) bytes of ""(.)""$")]
    public Task GivenHoldsBytes(string path, int count, char c) => WriteAsync(path, Encoding.UTF8.GetBytes(new string(c, count)));

    [Given(@"^the process has removed ""(.*)""$")]
    public async Task GivenRemoved(string path) => await Init.RemoveAsync(path);

    [Given("the console has run users")]
    [When("the console runs users")]
    public async Task WhenUsers() => printed = await host!.RefreshUsersAsync();

    [Then(@"^the console prints ""([^""]*)""$")]
    public void ThenPrints(string text) => Assert.Equal(Unescape(text), printed);

    [Then(@"^the host's log says ""([^""]*)""$")]
    public void ThenLog(string text) => Assert.Equal(Unescape(text), log.ToString());

    [Then(@"^""(.*)"" has the group ""(.*)"" and the mode (\d+)$")]
    public async Task ThenGroupAndMode(string path, string group, string mode)
    {
        Stat d = await Init.StatAsync(path);
        Assert.Equal((group, Convert.ToUInt32(mode, 8)), (d.Gid, d.Mode & 0b111_111_111));
    }

    [Then(@"^""(.*)"" has the mode (\d+)$")]
    public async Task ThenMode(string path, string mode) => Assert.Equal(Convert.ToUInt32(mode, 8), (await Init.StatAsync(path)).Mode & 0b111_111_111);

    [Given(@"^the host is to install ""([^""]*)"" holding ""([^""]*)""$")]
    public void GivenFile(string path, string text) => files[path] = Unescape(text);

    [Given(@"^the host is to install ""(.*)"" holding ""(.*)"" and ""(.*)"" holding ""(.*)""$")]
    public void GivenFiles(string first, string firstText, string second, string secondText)
    {
        files[first] = Unescape(firstText);
        files[second] = Unescape(secondText);
    }

    // The boot's program writes its arguments where its descriptors 1 and 2 are, and to 3, which
    // only shows if init gave it more than the console's three.
    [Given(@"^the host is to run ""(.*)"" at boot, a program that writes its arguments to descriptors 1 and 2 and to 3, and exits ""(.*)""$")]
    public void GivenBoot(string argv, string status) => Boot(argv, status, false);

    [Given(@"^the host is to run ""(.*)"" at boot, a program that writes its arguments to descriptors 1 and 2 and to 3, closes its descriptor 0, and exits ""(.*)""$")]
    public void GivenBootClosing(string argv, string status) => Boot(argv, status, true);

    [Then(@"^init's descriptor (\d+) is ""(.*)""$")]
    public async Task ThenInitDescriptor(int fd, string path) => Assert.Equal(path, await Init.Fd2PathAsync(fd));

    [Then(@"^init has no descriptor (\d+)$")]
    public async Task ThenNoInitDescriptor(int fd) => await Assert.ThrowsAsync<SyscallException>(async () => await Init.Fd2PathAsync(fd));

    [Then(@"^the console shows ""(.*)""$")]
    public void ThenConsole(string text) => Assert.Equal(Unescape(text), console.Written);

    [Then(@"^the boot status ends with ""(.*)""$")]
    public void ThenBootStatus(string status) => Assert.EndsWith(status, host!.BootStatus, StringComparison.Ordinal);

    [Then("the boot status is empty")]
    public void ThenBootStatusEmpty() => Assert.Equal(string.Empty, host!.BootStatus);

    [Given(@"^the host's nvram holds ""(.*)""$")]
    public void GivenNvram(string contents) => nvram = Encoding.UTF8.GetBytes(contents);

    [Then("the kernel's network and console are the host's")]
    public void ThenDevices() => Assert.True(ReferenceEquals(network, host!.Kernel.Host) && ReferenceEquals(console, host.Kernel.Console));

    [Then(@"^authenticating on ""(.*)"" for ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenFauthFails(string path, string aname, string error)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.ORDWR);
        Assert.Equal(error, (await Assert.ThrowsAsync<SyscallException>(() => Init.FauthAsync(fd, aname).AsTask())).Message);
        await Init.CloseAsync(fd);
    }

    [Then(@"^mounting ""(.*)"" on ""(.*)"" without authenticating fails with ""(.*)""$")]
    public async Task ThenMountFails(string path, string old, string error)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.ORDWR);
        Assert.Equal(error, (await Assert.ThrowsAsync<SyscallException>(() => Init.MountAsync(fd, -1, old, MountFlags.Replace, string.Empty).AsTask())).Message);
    }

    [Then(@"^the boot fails with ""(.*)""$")]
    public void ThenBootFails(string message) => Assert.Equal(message, failure?.Message);

    public void Dispose()
    {
        host?.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal);

    private void Boot(string argv, string status, bool closing)
    {
        boot = argv.Split(' ');
        programs = new Dictionary<string, ProgramMain>
        {
            [boot[0]] = async (process, args) =>
            {
                byte[] line = Encoding.UTF8.GetBytes(string.Join(' ', args) + "\n");
                foreach (int fd in new[] { 1, 2, 3 })
                {
                    try
                    {
                        await process.WriteAsync(fd, line);
                    }
                    catch (SyscallException)
                    {
                    }
                }

                if (closing)
                {
                    await process.CloseAsync(0);
                }

                process.Exits(status);
            },
        };
    }

    private async Task WriteAsync(string path, byte[] contents)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OWRITE | NinePConstants.OTRUNC);
        await Init.WriteAsync(fd, contents);
        await Init.CloseAsync(fd);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
