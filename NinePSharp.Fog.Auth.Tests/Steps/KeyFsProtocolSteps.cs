using System.Globalization;
using Dp9ik;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Fog.Auth.Tests.Support;
using NinePSharp.Messages;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests.Steps;

[Binding]
[Scope(Feature = "The keyfs tree speaks 9P2000 as keyfs.c does")]
public sealed class KeyFsProtocolSteps
{
    private const uint DMDIR = 0x80000000;
    private SoftwareTpm? tpm;
    private string directory = "";
    private KeyFsOptions options = null!;
    private KeyFsHost? host;
    private KeyFsClient keyfs = null!;
    private KeyFsClient? second;
    private Exception? failure;
    private object? reply;
    private Qid renamedQid;
    private byte[] rootListing = [];
    private int firstEntryLength;
    private byte[] rawReply = [];

    [Given(@"^a keyfs with the users ""(.*)"" and ""(.*)"", made in that order$")]
    public async Task GivenKeyFs(string first, string other)
    {
        tpm = SoftwareTpm.Start();
        directory = Directory.CreateTempSubdirectory("keyfs-").FullName;
        options = new KeyFsOptions
        {
            StateDirectory = directory,
            SocketPath = Path.Combine(directory, "keys.sock"),
            OpenTpm = tpm.OpenDevice,
        };
        host = await KeyFsHost.StartAsync(options, CancellationToken.None);
        using KeyFsClient setup = await KeyFsClient.AttachAsync(options.SocketPath);
        foreach (string user in new[] { first, other })
        {
            await setup.CreateAsync("", user, isDirectory: true);
            await setup.WriteAsync($"{user}/aeskey", AuthKey.FromPassword(user + "-password").AesKey);
        }
    }

    [Given("a 9P client attached to its admin socket")]
    public async Task GivenClient() => keyfs = await KeyFsClient.AttachAsync(options.SocketPath);

    [AfterScenario]
    public async Task CleanUpAsync()
    {
        second?.Dispose();
        keyfs?.Dispose();
        if (host is not null) await host.DisposeAsync();
        tpm?.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    // --- Version and fids --------------------------------------------------------

    [When(@"^the client negotiates version ""(.*)"" with message size (\d+)$")]
    public async Task WhenVersion(string version, uint size) => reply = await keyfs.Raw.VersionAsync(size, version);

    [Then(@"^the server answers version ""(.*)"" with message size (\d+)$")]
    public void ThenVersion(string version, uint size)
    {
        var answer = Assert.IsType<Rversion>(reply);
        Assert.Equal(version, answer.Version);
        Assert.Equal(size, answer.MSize);
    }

    [Then(@"^reading the root through fid 0 fails with ""(.*)""$")]
    public async Task ThenRootReadFails(string message)
    {
        failure = await CatchAsync(() => keyfs.Raw.ReadAsync(0, 0, 64));
        ThenError(message);
    }

    [When(@"^the client sends (walk|open|create|read|write|clunk|remove|stat|wstat) on fid (\d+), which is not in use$")]
    public async Task WhenUnusedFid(string request, uint fid)
    {
        NinePClient raw = keyfs.Raw;
        failure = await CatchAsync(request switch
        {
            "walk" => () => raw.WalkAsync(fid, fid + 1, []),
            "open" => () => raw.OpenAsync(fid, 0),
            "create" => () => raw.CreateAsync(fid, "scott", DMDIR | 0x1FF, 0),
            "read" => () => raw.ReadAsync(fid, 0, 64),
            "write" => () => raw.WriteAsync(fid, 0, [1]),
            "clunk" => () => raw.ClunkAsync(fid),
            "remove" => () => raw.RemoveAsync(fid),
            "stat" => () => raw.StatAsync(fid),
            _ => (Func<Task>)(() => raw.WstatAsync(fid, KeyFsClient.RenameStat("other"))),
        });
    }

    [Then(@"^it receives the error ""(.*)""$")]
    public void ThenError(string message)
    {
        Assert.True(failure is NinePException, failure?.ToString() ?? "the request succeeded");
        Assert.Equal(message, failure!.Message);
    }

    // --- Walks -------------------------------------------------------------------

    [Given(@"^fid (\d+) is walked to ""(.*)""$")]
    public async Task GivenWalked(uint fid, string path) => await keyfs.Raw.WalkAsync(KeyFsClient.RootFid, fid, path.Split('/'));

    [When(@"^fid (\d+) is walked to ""(.*)"" into fid (\d+)$")]
    public async Task WhenWalkedInto(uint from, string path, uint into)
        => failure = await CatchAsync(async () => reply = await keyfs.Raw.WalkAsync(from, into, path.Split('/')));

    [When(@"^fid (\d+) is walked through ""(.*)"" into fid (\d+)$")]
    public async Task WhenWalkedThrough(uint from, string names, uint into)
    {
        string[] parts = names.Length == 0 ? [] : names.Split('/');
        failure = await CatchAsync(async () => reply = await keyfs.Raw.WalkAsync(from, into, parts));
    }

    [When(@"^fid (\d+) is walked through (\d+) ""\.\."" names into fid (\d+)$")]
    public async Task WhenWalkedDotDot(uint from, int count, uint into)
        => failure = await CatchAsync(async () => reply = await keyfs.Raw.WalkAsync(from, into, Enumerable.Repeat("..", count).ToArray()));

    [Then(@"^the walk returns (\d+) qids and fid (\d+) names ""(.*)""$")]
    public async Task ThenWalkNames(int count, uint fid, string name)
    {
        Assert.Null(failure);
        Assert.Equal(count, Assert.IsType<Rwalk>(reply).Wqid.Length);
        Assert.Equal(name, (await keyfs.Raw.StatAsync(fid)).Stat.Name);
    }

    [Then(@"^the walk returns (\d+) qids and fid (\d+) is not in use$")]
    public async Task ThenPartialWalk(int count, uint fid)
    {
        Assert.Null(failure);
        Assert.Equal(count, Assert.IsType<Rwalk>(reply).Wqid.Length);
        failure = await CatchAsync(() => keyfs.Raw.StatAsync(fid));
        ThenError("stat on unattached fid");
    }

    [When(@"^fid (\d+) is clunked$")]
    public async Task WhenClunked(uint fid) => failure = await CatchAsync(() => keyfs.Raw.ClunkAsync(fid));

    [Then(@"^the clunk succeeds and fid (\d+) is not in use$")]
    public async Task ThenClunked(uint fid)
    {
        Assert.Null(failure);
        failure = await CatchAsync(() => keyfs.Raw.StatAsync(fid));
        ThenError("stat on unattached fid");
    }

    [Then(@"^fid (\d+) still names ""(.*)""$")]
    public async Task ThenStillNames(uint fid, string name) => Assert.Equal(name, (await keyfs.Raw.StatAsync(fid)).Stat.Name);

    // --- Qids --------------------------------------------------------------------

    [Then(@"^the qid of ""(.*)"" is a directory with path 0$")]
    public async Task ThenRootQid(string path)
    {
        Qid qid = await QidOf(path);
        Assert.Equal(QidType.QTDIR, qid.Type);
        Assert.Equal(0UL, qid.Path);
    }

    [Then(@"^the qid of ""(.*)"" is a (directory|file) with path (\d+) plus 256 times its user number$")]
    public async Task ThenQid(string path, string kind, ulong node)
    {
        Qid qid = await QidOf(path);
        Assert.Equal(kind == "directory" ? QidType.QTDIR : QidType.QTFILE, qid.Type);
        Assert.Equal(node, qid.Path % 256);
        Assert.Equal(UserNumber(await QidOf(path.Split('/')[0])), qid.Path / 256);
        Assert.NotEqual(0UL, qid.Path / 256);
    }

    [Then(@"^""(.*)"" and ""(.*)"" have different user numbers$")]
    public async Task ThenDistinctUsers(string first, string other)
        => Assert.NotEqual(UserNumber(await QidOf(first)), UserNumber(await QidOf(other)));

    [When(@"^""(.*)"" is renamed to ""(.*)"" over 9P$")]
    public async Task WhenRenamed(string path, string name)
    {
        if (path.Length > 0 && !path.Contains('/', StringComparison.Ordinal)) renamedQid = await QidOf(path);
        failure = await CatchAsync(() => keyfs.RenameAsync(path, name));
    }

    [Then(@"^the qid of ""(.*)"" equals the qid ""(.*)"" had$")]
    public async Task ThenQidKept(string path, string old)
    {
        Assert.Null(failure);
        Assert.Equal(renamedQid, await QidOf(path));
    }

    // --- Open and create -----------------------------------------------------------

    [When(@"^""(.*)"" is opened for (reading|writing|truncation)$")]
    public async Task WhenOpened(string path, string mode)
    {
        uint fid = await keyfs.WalkAsync(path);
        byte bits = mode switch { "reading" => 0, "writing" => 1, _ => 0x10 };
        failure = await CatchAsync(async () => reply = await keyfs.Raw.OpenAsync(fid, bits));
    }

    [Then(@"^the open reports the qid of ""(.*)"" and I/O unit (\d+)$")]
    public async Task ThenOpenReports(string path, uint iounit)
    {
        Assert.Null(failure);
        var opened = Assert.IsType<Ropen>(reply);
        Assert.Equal(await QidOf(path), opened.Qid);
        Assert.Equal(iounit, opened.Iounit);
    }

    [When(@"^the client creates the directory ""(.*)"" in the root$")]
    public async Task WhenCreated(string name)
    {
        uint fid = await keyfs.WalkAsync("");
        failure = await CatchAsync(async () => reply = await keyfs.Raw.CreateAsync(fid, name, DMDIR | 0x1FF, 0));
    }

    [Then(@"^the create reports the qid of ""(.*)"" and I/O unit (\d+)$")]
    public async Task ThenCreateReports(string path, uint iounit)
    {
        Assert.Null(failure);
        var created = Assert.IsType<Rcreate>(reply);
        Assert.Equal(await QidOf(path), created.Qid);
        Assert.Equal(iounit, created.Iounit);
    }

    // --- Directory reads and stat ----------------------------------------------------

    [Then(@"^the root lists ""(.*)""$")]
    public async Task ThenRootLists(string names)
        => Assert.Equal(names.Split(", "), (await keyfs.ListAsync("")).Select(entry => entry.Name).ToArray());

    [When("the root is read with a count one byte smaller than its first entry")]
    public async Task WhenReadSmall()
    {
        await MeasureRoot();
        reply = await ReadRoot(0, (uint)firstEntryLength - 1);
    }

    [When("the root is read with a count exactly the size of its first entry")]
    public async Task WhenReadExact() => reply = await ReadRoot(0, (uint)firstEntryLength);

    [When("the root is read from the offset just past its first entry")]
    public async Task WhenReadPast() => reply = await ReadRoot((ulong)firstEntryLength, 8192);

    [Then("the read returns no entries")]
    public void ThenNoEntries() => Assert.Equal(0u, Assert.IsType<Rread>(reply).Count);

    [Then(@"^the read returns exactly the entry ""(.*)""$")]
    public void ThenExactlyEntry(string name)
    {
        byte[] data = Assert.IsType<Rread>(reply).Data.ToArray();
        int offset = 0;
        Stat entry = new(data, ref offset);
        Assert.Equal(name, entry.Name);
        Assert.Equal(data.Length, offset);
    }

    [Then(@"^the stat of ""(.*)"" is named ""(.*)"" with mode (0x[0-9A-F]+), length 0 and owner ""(.*)""$")]
    public async Task ThenStat(string path, string name, string mode, string owner)
    {
        Stat stat = await keyfs.StatAsync(path);
        Assert.Equal(name, stat.Name);
        Assert.Equal(uint.Parse(mode[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), stat.Mode);
        Assert.Equal(0UL, stat.Length);
        Assert.Equal(owner, stat.Uid);
        Assert.Equal(owner, stat.Gid);
        Assert.Equal(owner, stat.Muid);
        Assert.Equal(await QidOf(path), stat.Qid);
    }

    // --- Other requests and connections -------------------------------------------

    [When(@"^a raw connection sends a 9P2000\.L getattr with tag (\d+)$")]
    public async Task WhenRawGetattr(ushort tag)
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(options.SocketPath));
        await using var stream = new System.Net.Sockets.NetworkStream(socket);
        await SendRawAsync(stream, new Tversion(NinePConstants.NoTag, 8192, "9P2000"));
        await ReadRawAsync(stream);
        await SendRawAsync(stream, new Tgetattr(tag, 0, 0x7FF));
        rawReply = await ReadRawAsync(stream);
    }

    [Then(@"^the raw connection receives the error ""(.*)"" with tag (\d+)$")]
    public void ThenRawError(string message, ushort tag)
    {
        Assert.Equal((byte)MessageTypes.Rerror, rawReply[4]);
        var error = new Rerror(rawReply);
        Assert.Equal(tag, error.Tag);
        Assert.Equal(message, error.Ename);
    }

    [When(@"^the client flushes tag (\d+)$")]
    public async Task WhenFlushed(ushort tag) => failure = await CatchAsync(() => keyfs.Raw.FlushAsync(tag));

    [Then("the flush is answered")]
    public void ThenFlushAnswered() => Assert.Null(failure);

    [Given("a second 9P client attached to the admin socket")]
    public async Task GivenSecond() => second = await KeyFsClient.AttachAsync(options.SocketPath);

    [When(@"^the first client walks fid 0 to ""(.*)"" into fid (\d+)$")]
    public async Task WhenFirstWalks(string path, uint fid) => await keyfs.Raw.WalkAsync(0, fid, path.Split('/'));

    [Then(@"^the second client's fid (\d+) is not in use$")]
    public async Task ThenSecondUnused(uint fid)
    {
        failure = await CatchAsync(() => second!.Raw.StatAsync(fid));
        ThenError("stat on unattached fid");
    }

    [When(@"^(\d+) more clients attach and disconnect$")]
    public async Task WhenClientsComeAndGo(int count)
    {
        for (int index = 0; index < count; index++)
        {
            using KeyFsClient transient = await KeyFsClient.AttachAsync(options.SocketPath);
            await transient.ListAsync("");
        }
    }

    [Then("the keyfs holds fids for only the open connection")]
    public async Task ThenOneSession()
    {
        // Closing is noticed asynchronously by the connection loop.
        for (int attempt = 0; attempt < 100 && host!.Dispatcher.SessionCount != 1; attempt++) await Task.Delay(50);
        Assert.Equal(1, host!.Dispatcher.SessionCount);
    }

    [Then("the admin listener tracks only the open connection")]
    public async Task ThenOneConnection()
    {
        for (int attempt = 0; attempt < 100 && host!.AdminConnections != 1; attempt++) await Task.Delay(50);
        Assert.Equal(1, host!.AdminConnections);
    }

    // --- Helpers -------------------------------------------------------------------

    private async Task<Qid> QidOf(string path) => (await keyfs.StatAsync(path)).Qid;

    private static async Task SendRawAsync(Stream stream, ISerializable message)
    {
        var bytes = new byte[message.Size];
        message.WriteTo(bytes);
        await stream.WriteAsync(bytes);
    }

    private static async Task<byte[]> ReadRawAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var message = new byte[BitConverter.ToUInt32(header)];
        header.CopyTo(message, 0);
        await stream.ReadExactlyAsync(message.AsMemory(4));
        return message;
    }

    private static ulong UserNumber(Qid qid) => qid.Path / 256;

    private async Task MeasureRoot()
    {
        rootListing = await keyfs.ReadAsync("");
        int offset = 0;
        _ = new Stat(rootListing, ref offset);
        firstEntryLength = offset;
    }

    private async Task<Rread> ReadRoot(ulong offset, uint count)
    {
        uint fid = await keyfs.WalkAsync("");
        await keyfs.Raw.OpenAsync(fid, 0);
        return await keyfs.Raw.ReadAsync(fid, offset, count);
    }

    private static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }
}
