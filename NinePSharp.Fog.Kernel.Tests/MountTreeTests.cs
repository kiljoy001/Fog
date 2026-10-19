using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

// The mount driver's tree over a scripted server: the fids it walks, keeps, clunks and lets go.
public sealed class MountTreeTests : IAsyncDisposable
{
    private readonly ScriptedServer server = new();

    [Fact]
    public async Task RemovingAnOpenFileRemovesItThroughTheFidItIsOpenOn()
    {
        MountTree tree = await AttachAsync();
        ResourceHandle file = await WalkAsync(tree, "notes");
        ResourceOpenHandle open = await Bounded(tree.OpenAsync(file, NinePConstants.OREAD, default));
        uint openFid = new Topen(server.Of(MessageTypes.Topen).Last()).Fid;

        await Bounded(tree.RemoveAsync(file, open, default));

        Assert.Equal(openFid, new Tremove(server.Of(MessageTypes.Tremove).Single()).Fid);
        await Assert.ThrowsAsync<IOException>(() => tree.ReadAsync(open, 0, 1, default));
    }

    [Fact]
    public async Task RemovingTheRootRemovesAClonedFidAndForgetsNothing()
    {
        MountTree tree = await AttachAsync();

        await Bounded(tree.RemoveAsync(tree.Root, null, default));

        Assert.NotEqual(tree.RootFid, new Tremove(server.Of(MessageTypes.Tremove).Single()).Fid);
    }

    [Fact]
    public async Task ACreatedFileIsWalkedToByItsName()
    {
        MountTree tree = await AttachAsync();
        ResourceOpenHandle created = await Bounded(tree.CreateAsync(tree.Root, "new", 0b110_100_100, NinePConstants.ORDWR, default));
        await Bounded(tree.ClunkAsync(created));

        Assert.Equal("new", (await Bounded(tree.StatAsync(created.Resource, default))).Name);
    }

    [Fact]
    public async Task ACreateTheServerRefusesClunksTheFidItCloned()
    {
        server.RefuseCreates = true;
        MountTree tree = await AttachAsync();

        await Assert.ThrowsAsync<MountErrorException>(() => tree.CreateAsync(tree.Root, "new", 0b110_100_100, NinePConstants.ORDWR, default).WaitAsync(KernelDriver.Bound));

        Assert.Equal([new Tcreate(server.Of(MessageTypes.Tcreate).Single()).Fid], server.Clunked);
    }

    [Fact]
    public async Task AReadAsksForWhatIsLeftInPiecesOfTheIounit()
    {
        server.IoUnit = 4;
        MountTree tree = await AttachAsync();
        ResourceOpenHandle open = await Bounded(tree.OpenAsync(await WalkAsync(tree, "notes"), NinePConstants.OREAD, default));
        ReadOnlyMemory<byte> read = await Bounded(tree.ReadAsync(open, 0, 10, default));

        Assert.Equal(ScriptedServer.Data, read.ToArray());
        Assert.Equal([4u, 4u, 2u], server.Of(MessageTypes.Tread).Select(message => new Tread(message).Count));
    }

    [Fact]
    public async Task ClosingAFileTwiceClunksItOnce()
    {
        MountTree tree = await AttachAsync();
        ResourceOpenHandle open = await Bounded(tree.OpenAsync(await WalkAsync(tree, "notes"), NinePConstants.OREAD, default));
        await Bounded(tree.ClunkAsync(open));
        await Bounded(tree.ClunkAsync(open));

        Assert.Single(server.Clunked);
    }

    [Fact]
    public async Task AFileTheTreeNeverSawDoesNotExist()
    {
        MountTree tree = await AttachAsync();

        Assert.Equal(Errors.NotExist, (await Assert.ThrowsAsync<IOException>(() => tree.StatAsync(tree.Handle(new Qid(QidType.QTFILE, 0, 99)), default))).Message);
    }

    [Fact]
    public async Task AFileWalkedToAgainAsAnotherIsClunkedAndDoesNotExist()
    {
        MountTree tree = await AttachAsync();
        ResourceHandle a = (await Bounded(tree.ReadDirectoryAsync(tree.Root, default))).Single(entry => entry.Name == "a").Handle;
        server.Paths["a"] = 9;
        int clunks = server.Clunked.Count();

        Assert.Equal(Errors.NotExist, (await Assert.ThrowsAsync<IOException>(() => tree.StatAsync(a, default))).Message);
        Assert.Equal(new Twalk(server.Of(MessageTypes.Twalk).Last()).NewFid, server.Clunked.Skip(clunks).Single());
    }

    [Fact]
    public async Task AFidWalkedForAListedFileIsLetGoOnceAnotherIsUsedMoreLately()
    {
        MountTree tree = await AttachAsync(bound: 1);
        IReadOnlyList<ResourceDirectoryEntry> entries = await Bounded(tree.ReadDirectoryAsync(tree.Root, default));
        await Bounded(tree.StatAsync(entries.Single(entry => entry.Name == "a").Handle, default));
        uint a = new Twalk(server.Of(MessageTypes.Twalk).Last()).NewFid;
        await Bounded(tree.StatAsync(entries.Single(entry => entry.Name == "b").Handle, default));

        Assert.Contains(a, server.Clunked);
    }

    [Fact]
    public async Task AFileForgottenWhileInUseKeepsItsFidUntilTheUseEnds()
    {
        MountTree tree = await AttachAsync();
        ResourceHandle a = await WalkAsync(tree, "a");
        uint fid = new Twalk(server.Of(MessageTypes.Twalk).Last()).NewFid;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldStats = hold.Task;
        Task<ResourceStat> stat = tree.StatAsync(a, default);
        await server.StatHeld.Task.WaitAsync(KernelDriver.Bound);
        server.HoldStats = null;
        await Bounded(tree.RemoveAsync(a, null, default));

        Assert.DoesNotContain(fid, server.Clunked);
        hold.SetResult();
        await Bounded(stat);
        Assert.Contains(fid, server.Clunked);
    }

    [Fact]
    public async Task AForgottenFileLeavesNoTraceForTheFidsLetGoLater()
    {
        MountTree tree = await AttachAsync(bound: 1);
        ResourceHandle a = await WalkAsync(tree, "a");
        await Bounded(tree.RemoveAsync(a, null, default));
        await WalkAsync(tree, "b");
        uint b = new Twalk(server.Of(MessageTypes.Twalk).Last()).NewFid;
        await WalkAsync(tree, "c");

        Assert.Contains(b, server.Clunked);
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    private static Task<T> Bounded<T>(Task<T> task) => task.WaitAsync(KernelDriver.Bound);

    private static Task Bounded(Task task) => task.WaitAsync(KernelDriver.Bound);

    private static async Task<ResourceHandle> WalkAsync(MountTree tree, string name) => (await Bounded(tree.WalkAsync(tree.Root, name, default)))!;

    private async Task<MountTree> AttachAsync(int bound = 8)
    {
        var connection = new MountConnection(server.Peer, server.Peer.Handle, server.Peer, () => ScriptedPeer.Context);
        await Bounded(connection.VersionAsync());
        return await Bounded(MountTree.AttachAsync(connection, "#M0", "glenda", string.Empty, bound));
    }
}
