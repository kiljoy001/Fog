using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using NinePSharp.Server.FileSystem;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "mount speaks 9P to a file server over a descriptor, as 9front's devmnt does")]
public sealed class MountSteps(KernelDriver driver) : IAsyncDisposable
{
    private static readonly TimeSpan Bound = KernelDriver.Bound;
    private readonly FileServerHarness server = new();
    private readonly CancellationTokenSource cancel = new();
    private int serverEnd;
    private string? failure;
    private Task<ReadOnlyMemory<byte>>? read;
    private string[] childReads = [];
    private byte[] written = [];
    private int fidsBefore;
    private int walksBefore;
    private string deepest = string.Empty;

    private Process Init => driver.Init;

    [Given(@"^a 9P file server whose tree holds ""(.*)"" with ""(.*)"" and an empty directory ""(.*)""$")]
    public void GivenServer(string file, string contents, string directory)
    {
        server.Root.AddChild(new NinePFile(file, Encoding.UTF8.GetBytes(contents)));
        server.Root.AddChild(new NinePDir(directory));
    }

    [Given(@"^the process has the client end of a connection to it as descriptor (\d+)$")]
    public async Task GivenConnection(int fd)
    {
        var (client, end) = await Init.PipeAsync();
        await Init.DupAsync(client, fd);
        await Init.CloseAsync(client);
        serverEnd = end;
        server.Serve(Init, serverEnd);
    }

    [Given(@"^the server refuses to open ""(.*)"" with ""(.*)""$")]
    public void GivenRefusal(string file, string error) => server.Refusal = (file, error);

    [Given("the server holds reads of \"notes\" until they are released")]
    public void GivenHeld() => server.HoldReads = true;

    [Given(@"^the server's tree holds the files ""f1"" to ""f(\d+)""$")]
    public void GivenFiles(int count)
    {
        for (int i = 1; i <= count; i++)
        {
            server.Root.AddChild(new NinePFile($"f{i}", Encoding.UTF8.GetBytes($"file {i}")));
        }
    }

    [Given(@"^the mount driver holds at most (\d+) fids for walked files$")]
    public void GivenFidBound(int fids) => driver.Kernel.MountFids = fids;

    [When(@"^the process mounts descriptor (\d+) on ""(.*)""$")]
    public Task WhenMounts(int fd, string old) => Init.MountAsync(fd, -1, old, MountFlags.Replace, string.Empty).AsTask().WaitAsync(Bound);

    [When(@"^the process mounts descriptor (\d+) on ""(.*)"" with creation$")]
    public Task WhenMountsCreating(int fd, string old)
        => Init.MountAsync(fd, -1, old, MountFlags.Replace | MountFlags.Create, string.Empty).AsTask().WaitAsync(Bound);

    [When(@"^the process creates ""(.*)"" holding ""(.*)""$")]
    public Task WhenCreates(string path, string contents) => WriteFileAsync(path, Encoding.UTF8.GetBytes(contents));

    [When(@"^the process opens ""(.*)"" for reading$")]
    public async Task WhenOpens(string path)
    {
        try
        {
            await Init.OpenAsync(path, NinePConstants.OREAD).AsTask().WaitAsync(Bound);
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When(@"^the process opens ""(.*)"" for reading as descriptor 0$")]
    public async Task WhenOpensZero(string path) => Assert.Equal(0, await Init.OpenAsync(path, NinePConstants.OREAD).AsTask().WaitAsync(Bound));

    [When(@"^the process writes (\d+) bytes to ""(.*)""$")]
    public Task WhenWritesBytes(int count, string path)
    {
        written = new byte[count];
        new Random(43).NextBytes(written);
        return WriteFileAsync(path, written);
    }

    [When(@"^(\d+) children each read ""(.*)"" at once$")]
    public async Task WhenChildrenRead(int count, string path)
    {
        childReads = new string[count];
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int i = 0; i < count; i++)
        {
            int child = i;
            Init.Fork(RforkFlags.Fdg, async process =>
            {
                await started.Task;
                childReads[child] = await ReadFileAsync(process, path);
                process.Exits(string.Empty);
            });
        }

        started.SetResult();
        for (int i = 0; i < count; i++)
        {
            await Init.WaitAsync().WaitAsync(Bound);
        }
    }

    [When(@"^the process starts reading ""(.*)""$")]
    public async Task WhenStartsReading(string path)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD).AsTask().WaitAsync(Bound);
        read = Init.ReadAsync(fd, 100, cancel.Token).AsTask();
    }

    [When("the read is cancelled")]
    public void WhenCancelled() => cancel.Cancel();

    [When(@"^the process stats ""/n/f(\d+)"" to ""/n/f(\d+)""$")]
    public async Task WhenStatsFiles(int first, int count)
    {
        for (int i = first; i <= count; i++)
        {
            await Init.StatAsync($"/n/f{i}").AsTask().WaitAsync(Bound);
        }
    }

    [When(@"^the process changes to ""(.*)""$")]
    public Task WhenChanges(string path) => Init.ChdirAsync(path).AsTask().WaitAsync(Bound);

    [When(@"^the server's ""(.*)"" is replaced by a new directory$")]
    public async Task WhenReplaced(string name)
    {
        await server.Root.RemoveAsync(name, CancellationToken.None);
        server.Root.AddChild(new NinePDir(name));
    }

    [Given(@"^the server gives opens an iounit of (\d+)$")]
    public void GivenIoUnit(uint iounit) => server.IoUnit = iounit;

    [Given(@"^the server answers at most (\d+) bytes of a read and takes at most (\d+) of a write$")]
    public void GivenLimits(int read, int write) => (server.ReadLimit, server.WriteLimit) = (read, write);

    [Given(@"^the server refuses Tversion with ""(.*)""$")]
    public void GivenVersionRefusal(string why) => server.VersionRefusal = why;

    [Given(@"^the server's tree holds 20 directories, one in another, and the files ""f1"" to ""f4""$")]
    public void GivenDeep()
    {
        INinePNode directory = server.Root;
        var names = new List<string>();
        for (int i = 1; i <= 20; i++)
        {
            var next = new NinePDir($"d{i}");
            ((NinePDir)directory).AddChild(next);
            directory = next;
            names.Add($"d{i}");
        }

        deepest = "/n/" + string.Join('/', names);
        GivenFiles(4);
    }

    [When("the server's fids are counted")]
    public void WhenFidsCounted() => fidsBefore = server.LiveFids;

    [When("the server's Twalks are counted")]
    public void WhenWalksCounted() => walksBefore = server.Walks.Count;

    [When(@"^the process lists ""(.*)""$")]
    public async Task WhenLists(string path)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD).AsTask().WaitAsync(Bound);
        await Init.DirReadAsync(fd);
        await Init.CloseAsync(fd);
    }

    [When(@"^the process tries to create ""(.*)""$")]
    public async Task WhenTriesCreate(string path)
    {
        try
        {
            await Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_100).AsTask().WaitAsync(Bound);
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When(@"^the process removes ""(.*)""$")]
    public Task WhenRemoves(string path) => Init.RemoveAsync(path).AsTask().WaitAsync(Bound);

    [When(@"^the process opens ""(.*)"" with ORCLOSE and closes it$")]
    public async Task WhenOrclose(string path)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD | NinePConstants.ORCLOSE).AsTask().WaitAsync(Bound);
        await Init.CloseAsync(fd).AsTask().WaitAsync(Bound);
    }

    [When(@"^the process changes the mode of ""(.*)"" to (\d+)$")]
    public Task WhenChmods(string path, string mode) => Init.WStatAsync(path, Moded(mode)).AsTask().WaitAsync(Bound);

    [When(@"^the process opens ""(.*)"" and changes its mode through the descriptor to (\d+)$")]
    public async Task WhenChmodsOpen(string path, string mode)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD).AsTask().WaitAsync(Bound);
        await Init.FWStatAsync(fd, Moded(mode)).AsTask().WaitAsync(Bound);
        await Init.CloseAsync(fd);
    }

    [Given(@"^the server's tree holds the directories ""(.*)"", ""(.*)"" and ""(.*)""$")]
    public void GivenDirectories(string a, string b, string c)
    {
        foreach (string name in new[] { a, b, c })
        {
            server.Root.AddChild(new NinePDir(name));
        }
    }

    [When(@"^the process stats ""([^""]*)""$")]
    public Task WhenStats(string path) => Init.StatAsync(path).AsTask().WaitAsync(Bound);

    [When("the process changes to the deepest directory")]
    public Task WhenChangesDeep() => Init.ChdirAsync(deepest).AsTask().WaitAsync(Bound);

    [When(@"^the server's ""(.*)"" loses ""(.*)""$")]
    public async Task WhenLoses(string path, string child)
    {
        INinePNode node = server.Root;
        foreach (string name in path.Split('/'))
        {
            node = (await node.WalkAsync(name, CancellationToken.None))!;
        }

        await ((NinePDir)node).RemoveAsync(child, CancellationToken.None);
    }

    [When(@"^the process duplicates descriptor (\d+) as descriptor (\d+)$")]
    public Task WhenDuplicates(int fd, int target) => Init.DupAsync(fd, target).AsTask();

    [When(@"^the process tries to mount descriptor (\d+) on ""(.*)""$")]
    public async Task WhenTriesMount(int fd, string old)
    {
        try
        {
            await Init.MountAsync(fd, -1, old, MountFlags.Replace, string.Empty).AsTask().WaitAsync(Bound);
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [When("the server hangs up")]
    public Task WhenHangsUp() => server.HangUpAsync(Init, serverEnd);

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReading(string path, string contents) => Assert.Equal(contents, await ReadFileAsync(Init, path));

    [Then(@"^reading ""(.*)"" gives the same (\d+) bytes$")]
    public async Task ThenReadingBytes(string path, int count)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD).AsTask().WaitAsync(Bound);
        var all = new List<byte>();
        for (ReadOnlyMemory<byte> piece; (piece = await Init.ReadAsync(fd, 65536).AsTask().WaitAsync(Bound)).Length > 0;)
        {
            all.AddRange(piece.ToArray());
        }

        Assert.Equal(count, all.Count);
        Assert.Equal(written, all);
    }

    [Then(@"^reading ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenReadingFails(string path, string message)
        => Assert.StartsWith(message, (await Assert.ThrowsAsync<SyscallException>(() => ReadFileAsync(Init, path))).Message);

    [Then(@"^the names in ""(.*)"" are ""(.*)""$")]
    public async Task ThenNames(string path, string names)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD).AsTask().WaitAsync(Bound);
        Assert.Equal(names, string.Join(' ', (await Init.DirReadAsync(fd)).Select(entry => entry.Name!).Order(StringComparer.Ordinal)));
    }

    [Then(@"^descriptor (\d+) is not open$")]
    public async Task ThenNotOpen(int fd)
        => Assert.Equal(Errors.BadFd, (await Assert.ThrowsAsync<SyscallException>(() => Init.FStatAsync(fd).AsTask())).Message);

    [Then(@"^the server's ""(.*)"" holds ""(.*)""$")]
    public async Task ThenServerHolds(string path, string contents)
    {
        INinePNode node = server.Root;
        foreach (string name in path.Split('/'))
        {
            node = (await node.WalkAsync(name, CancellationToken.None))!;
        }

        Assert.Equal(contents, Encoding.UTF8.GetString(await node.ReadAsync(0, 8192, CancellationToken.None)));
    }

    [Then(@"^""(.*)"" is a (file|directory) of device ""(.)""$")]
    public async Task ThenDevice(string path, string kind, char device)
    {
        Stat stat = await Init.StatAsync(path);
        Assert.Equal((device, kind == "directory"), ((char)stat.Type, (stat.Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0));
    }

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);

    [Then(@"^no message on the connection carried more than (\d+) bytes of data$")]
    public void ThenLargest(int bytes) => Assert.InRange(server.LargestData, 1, bytes);

    [Then(@"^each child read ""(.*)""$")]
    public void ThenEachRead(string contents) => Assert.All(childReads, text => Assert.Equal(contents, text));

    [Then(@"^the largest message on the connection carried (\d+) bytes of data$")]
    public void ThenLargestExactly(int bytes) => Assert.Equal(bytes, server.LargestData);

    [Then(@"^writing ""(.*)"" to a new ""(.*)"" writes (\d+) bytes$")]
    public async Task ThenShortWrite(string text, string path, int count)
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_100).AsTask().WaitAsync(Bound);
        Assert.Equal(count, await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text)).AsTask().WaitAsync(Bound));
        await Init.CloseAsync(fd);
    }

    [Then("the server holds the fids it had")]
    public void ThenFidsAsBefore() => Assert.Equal(fidsBefore, server.LiveFids);

    [Then(@"^the server holds exactly (\d+) fids$")]
    public void ThenFidsExactly(int fids) => Assert.Equal(fids, server.LiveFids);

    [Then(@"^the server's tree has no ""(.*)""$")]
    public async Task ThenNoFile(string name) => Assert.Null(await server.Root.WalkAsync(name, CancellationToken.None));

    [Then(@"^the connection carried (\d+) Twstats$")]
    public void ThenWStats(int count) => Assert.Equal(count, server.WStats);

    [Then(@"^the server's Twalks since carried (\d+) names and then (\d+)$")]
    public void ThenWalkedInPieces(int first, int second)
    {
        int[] since = [.. server.Walks.Skip(walksBefore)];
        Assert.Contains((first, second), since.Zip(since.Skip(1)));
    }

    [Then(@"^stating ""(.*)"" again walks no names$")]
    public async Task ThenNoWalk(string path)
    {
        await Init.StatAsync(path).AsTask().WaitAsync(Bound);
        Assert.Equal(walksBefore, server.Walks.Count);
    }

    [Then(@"^descriptor (\d+) can be closed$")]
    public Task ThenClosable(int fd) => Init.CloseAsync(fd).AsTask().WaitAsync(Bound);

    [Then("the server was asked to open a file with ORCLOSE")]
    public void ThenOrclose() => Assert.Contains(server.OpenModes, mode => (mode & NinePConstants.ORCLOSE) != 0);

    [Then("the connection carried one Tversion")]
    public void ThenOneVersion() => Assert.Equal(1, server.Versions);

    [Then("the read was cancelled")]
    public Task ThenReadCancelled() => Assert.ThrowsAnyAsync<OperationCanceledException>(() => read!.WaitAsync(Bound));

    [Then(@"^the read fails with ""(.*)""$")]
    public async Task ThenReadFails(string message)
        => Assert.Equal(message, (await Assert.ThrowsAsync<SyscallException>(() => read!.WaitAsync(Bound))).Message);

    [Then("the connection carried a Tflush for the read")]
    public void ThenFlushed() => Assert.Contains(server.Flushed, tag => server.ReadTags.Contains(tag));

    [Then(@"^once reads are released, reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReleased(string path, string contents)
    {
        server.HoldReads = false;
        server.Release();
        Assert.Equal(contents, await ReadFileAsync(Init, path));
    }

    [Then(@"^the server holds at most (\d+) fids$")]
    public void ThenFids(int fids) => Assert.InRange(server.LiveFids, 1, fids);

    [Then(@"^stating ""(.*)"" again gives the file ""(.*)""$")]
    public async Task ThenStatAgain(string path, string name) => Assert.Equal(name, (await Init.StatAsync(path).AsTask().WaitAsync(Bound)).Name);

    [Then(@"^stating ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenStatFails(string path, string message)
        => Assert.StartsWith(message, (await Assert.ThrowsAsync<SyscallException>(() => Init.StatAsync(path).AsTask().WaitAsync(Bound))).Message);

    [Then(@"^reading descriptor 0 gives ""(.*)""$")]
    public async Task ThenReadingZero(string contents)
        => Assert.Equal(contents, Encoding.UTF8.GetString((await Init.ReadAsync(0, 100).AsTask().WaitAsync(Bound)).Span));

    public async ValueTask DisposeAsync()
    {
        server.Release();
        cancel.Dispose();
        await server.DisposeAsync();
    }

    private static Stat Moded(string mode)
    {
        Stat none = Process.NullDir;
        return new Stat(0, none.Type, none.Dev, none.Qid, Convert.ToUInt32(mode, 8), none.Atime, none.Mtime, none.Length, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static async Task<string> ReadFileAsync(Process process, string path)
    {
        int fd = await process.OpenAsync(path, NinePConstants.OREAD).AsTask().WaitAsync(Bound);
        string contents = Encoding.UTF8.GetString((await process.ReadAsync(fd, 8192).AsTask().WaitAsync(Bound)).Span);
        await process.CloseAsync(fd);
        return contents;
    }

    private async Task WriteFileAsync(string path, byte[] contents)
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_100).AsTask().WaitAsync(Bound);
        await Init.WriteAsync(fd, contents).AsTask().WaitAsync(Bound);
        await Init.CloseAsync(fd);
    }
}
