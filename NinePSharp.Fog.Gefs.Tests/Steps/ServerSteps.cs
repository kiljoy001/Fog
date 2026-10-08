using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Gefs.Tests.Support;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class ServerSteps(StoreContext context)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private string? path;
    private Process? init;
    private ulong sequence;

    private GefsFs Files => context.Files!;

    [Given(@"^a device of (\d+) blocks reamed with (\d+) arenas by (\w+) at time (\d+), served by a server$")]
    public void GivenServed(long blocks, int arenas, string owner, long time)
    {
        context.At(time);
        context.Device = new MemoryDevice(blocks);
        context.Store = Store.Ream(context.Device, arenas, owner, context.Clock);
        context.Server = new GefsServer(context.Device, context.Store, StoreContext.InGroup, context.Clock);
    }

    [Given(@"^a store made in a new file of (\d+) blocks with (\d+) arenas by (\w+)$")]
    public void GivenFile(long blocks, int arenas, string owner)
    {
        context.Server!.Dispose();
        path = Path.Combine(Path.GetTempPath(), $"gefs-{Guid.NewGuid():N}");
        context.Server = GefsServer.Create(path, blocks, arenas, owner, StoreContext.InGroup, context.Clock);
    }

    [When("the file is opened again")]
    public void WhenFileOpened() => context.Server = GefsServer.Open(path!, StoreContext.InGroup, context.Clock);

    [When("the device is opened again, served by a server")]
    public void WhenReopened()
    {
        context.Server!.Dispose();
        context.Store = Store.Open(context.Device!);
        context.Server = new GefsServer(context.Device!, context.Store, StoreContext.InGroup, context.Clock);
    }

    [When(@"^(\w+) attaches to ""(.*)""$")]
    public void WhenAttaches(string user, string aname) => context.Files = context.Server!.Attach(aname);

    [Then(@"^attaching to ""(.*)"" fails with ""(.*)""$")]
    public void ThenAttachFails(string aname, string error)
        => Assert.Equal(error, Assert.Throws<GefsException>(() => context.Server!.Attach(aname)).Message);

    [When(@"^(\d+) (?:more )?seconds? pass(?:es)?$")]
    public void WhenTimePasses(int seconds) => context.Clock.Advance(TimeSpan.FromSeconds(seconds));

    [When(@"^the server (snapshots|forks) (\w+) as (\w+)$")]
    public void WhenSnaps(string how, string label, string name) => context.Server!.Snap(label, name, how == "forks");

    [Then(@"^the store is at generation (\d+)$")]
    public void ThenGeneration(long generation) => Assert.Equal(generation, context.Store!.Generation);

    [When("the server is closed")]
    public void WhenClosed() => context.Server!.Dispose();

    [When("the device fails every write")]
    public void WhenWritesFail() => context.Device!.FailWrites();

    [Then(@"^the server's failure is ""(.*)""$")]
    public void ThenFailure(string message) => Assert.Equal(message, context.Server!.Failure!.Message);

    [Then(@"^no write reaches the device when (\d+) more seconds pass$")]
    public void ThenNoWrites(int seconds)
    {
        int writes = context.Device!.Writes;
        WhenTimePasses(seconds);
        Assert.Equal(writes, context.Device.Writes);
    }

    [Then(@"^(\w+) writing ""(.*)"" to (\S+) fails with ""(.*)""$")]
    public void ThenWriteFails(string user, string text, string file, string error)
    {
        ResourceOpenHandle open = Run(Files.OpenAsync(Walk(file), NinePConstants.OWRITE, Context(user), default));
        Assert.Equal(error, Assert.Throws<IOException>(() => Run(Files.WriteAsync(open, 0, Encoding.ASCII.GetBytes(text), Context(user), default))).Message);
    }

    [Then(@"^(\w+) opening (\S+) with truncation fails with ""(.*)""$")]
    public void ThenTruncateFails(string user, string file, string error)
        => Assert.Equal(error, Assert.Throws<IOException>(() => Run(Files.OpenAsync(Walk(file), NinePConstants.OWRITE | NinePConstants.OTRUNC, Context(user), default))).Message);

    [Then(@"^the server creating (\S+) in the root fails with ""(.*)""$")]
    public void ThenServerCreateFails(string name, string error)
        => Assert.Equal(error, Assert.Throws<IOException>(() => Run(Files.CreateAsync(Files.Root, name, false, default))).Message);

    [Then(@"^the server committing fails with ""(.*)""$")]
    public void ThenSyncFails(string error) => Assert.Equal(error, Assert.Throws<GefsException>(context.Server!.Sync).Message);

    [When(@"^(\d+) users each write (\d+) files at once, half through ""(\w+)"" and half through ""(\w+)""$")]
    public void WhenWritersRace(int writers, int files, string first, string second)
    {
        GefsFs[] trees = [context.Server!.Attach(first), context.Server.Attach(second)];
        Task[] running = [.. Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            GefsFs fs = trees[w * 2 / writers];
            for (int f = 0; f < files; f++)
            {
                string name = $"w{w}-{f}";
                ResourceOpenHandle open = Run(fs.CreateAndOpenAsync(fs.Root, name, 0b110_100_100, NinePConstants.OWRITE, Context("adm"), default));
                Run(fs.WriteAsync(open, 0, Encoding.ASCII.GetBytes(name), Context("adm"), default));
                Run(fs.ClunkAsync(open, Context("adm"), default));
            }
        }))];
        Assert.True(Task.WaitAll(running, Bound));
    }

    [Then(@"^main and work each hold the files written through them$")]
    public void ThenEachHolds()
    {
        foreach (var (label, writers) in new[] { ("main", Enumerable.Range(0, 4)), ("work", Enumerable.Range(4, 4)) })
        {
            GefsFs fs = context.Server!.Attach(label);
            string[] names = [.. writers.SelectMany(w => Enumerable.Range(0, 20).Select(f => $"w{w}-{f}"))];
            Assert.Equal(names.Order(), Run(fs.ReadDirectoryAsync(fs.Root, default)).Select(e => e.Name).Order());
            foreach (string name in names)
            {
                ResourceOpenHandle open = Run(fs.OpenAsync(Run(fs.WalkAsync(fs.Root, name, default))!, NinePConstants.OREAD, Context("adm"), default));
                Assert.Equal(name, Encoding.ASCII.GetString(Run(fs.ReadAsync(open, 0, 100, default)).Span));
                Run(fs.ClunkAsync(open, Context("adm"), default));
            }
        }
    }

    [When(@"^a kernel boots on it for (\w+)$")]
    public async Task WhenKernelBoots(string user)
        => init = await new FogKernel(new Dictionary<string, ProgramMain>(), Files, Files.Root, user).BootAsync().WaitAsync(Bound);

    [When(@"^the kernel's first process writes ""(.*)"" to ""(.*)""$")]
    public async Task WhenProcessWrites(string text, string file)
    {
        int fd = await init!.CreateAsync(file, NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(fd, Encoding.ASCII.GetBytes(text));
        await init.CloseAsync(fd);
    }

    [Then(@"^the kernel's first process reads ""(.*)"" from ""(.*)""$")]
    public async Task ThenProcessReads(string text, string file)
    {
        int fd = await init!.OpenAsync(file, NinePConstants.OREAD);
        Assert.Equal(text, Encoding.ASCII.GetString((await init.ReadAsync(fd, 100)).Span));
        await init.CloseAsync(fd);
    }

    [AfterScenario]
    public void Close()
    {
        context.Server?.Dispose();
        if (path is not null)
        {
            File.Delete(path);
        }
    }

    private static T Run<T>(ValueTask<T> operation) => operation.AsTask().GetAwaiter().GetResult();

    private static void Run(ValueTask operation) => operation.AsTask().GetAwaiter().GetResult();

    private ResourceOperationContext Context(string user) => new(new ResourceOperationId("server", Interlocked.Increment(ref sequence)), 1, user);

    private ResourceHandle Walk(string file)
    {
        ResourceHandle at = Files.Root;
        foreach (string name in file.Split('/'))
        {
            at = Run(Files.WalkAsync(at, name, default))!;
        }

        return at;
    }
}
