using System.Buffers.Binary;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

// devmnt's Mnt over a peer the test answers: the version, tags and fids it uses, how it hands
// replies to their callers, flushes, and what it makes of frames out of bounds or a peer that hangs up.
public sealed class MountConnectionTests
{
    private const int Bound = 3000;

    public static TheoryData<string, string> RefusedVersions => new()
    {
        { "Rerror", "no version for you" },
        { "Rflush", "unexpected reply type in fversion" },
        { "msize 32793", "server tries to increase msize in fversion" },
        { "msize 255", "nonsense value of msize in fversion" },
        { "version 9P1", "bad 9P version returned from server" },
    };

    [Theory]
    [MemberData(nameof(RefusedVersions))]
    public async Task AVersionReplyDevmntRefusesFailsTheMount(string reply, string error)
    {
        await using var peer = new ScriptedPeer
        {
            Version = (tag, version) => reply switch
            {
                "Rerror" => new Rerror(tag, error),
                "Rflush" => new Rflush(tag),
                "msize 32793" => new Rversion(tag, 32793, "9P2000"),
                "msize 255" => new Rversion(tag, 255, "9P2000"),
                _ => new Rversion(tag, version.MSize, "9P1"),
            },
        };
        var connection = new MountConnection(peer, peer.Handle, peer, () => ScriptedPeer.Context);

        Assert.Equal(error, (await Assert.ThrowsAsync<IOException>(() => connection.VersionAsync().WaitAsync(KernelDriver.Bound))).Message);
    }

    [Fact]
    public async Task AVersionTakesTheServersSmallerMessageSizeAndAnyNinePTwoThousandVariant()
    {
        await using var peer = new ScriptedPeer { Version = (tag, _) => new Rversion(tag, 256, "9P2000.u") };
        var smallest = new MountConnection(peer, peer.Handle, peer, () => ScriptedPeer.Context);
        await smallest.VersionAsync().WaitAsync(KernelDriver.Bound);
        (MountConnection largest, _) = await ConnectAsync();

        Assert.Equal((256, MountConnection.MaxRpc), (smallest.MessageSize, largest.MessageSize));
    }

    [Fact]
    public async Task FidsCountUpAndPassOverNoFid()
    {
        (MountConnection connection, _) = await ConnectAsync(firstFid: NinePConstants.NoFid - 1);

        Assert.Equal([NinePConstants.NoFid - 1, 0u, 1u], [connection.NewFid(), connection.NewFid(), connection.NewFid()]);
    }

    [Fact]
    public async Task TagsCountUpFromOnePassingOverZeroNoTagAndATagStillInUse()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        Task<byte[]> held = StatAsync(connection, 1);
        Assert.Equal(1, ScriptedPeer.Tag(await peer.RequestAsync(0)));

        var seen = new HashSet<ushort>();
        for (int i = 1; i <= ushort.MaxValue; i++)
        {
            Task<byte[]> stat = StatAsync(connection, 1);
            ushort tag = ScriptedPeer.Tag(await peer.RequestAsync(i));
            seen.Add(tag);
            peer.Send(StatReply(tag));
            await stat.WaitAsync(TimeSpan.FromMilliseconds(Bound));
        }

        Assert.Equal(ushort.MaxValue - 2, seen.Count);
        Assert.DoesNotContain((ushort)0, seen);
        Assert.DoesNotContain(NinePConstants.NoTag, seen);
        Assert.DoesNotContain((ushort)1, seen);
        Assert.Equal(2, ScriptedPeer.Tag(await peer.RequestAsync(1)));
        Assert.False(held.IsCompleted);
    }

    [Fact]
    public async Task TheServersErrorIsTheCallsAndAReplyOfAnotherTypeIsAnRpcError()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        Task<byte[]> refused = StatAsync(connection, 1);
        peer.Reply(new Rerror(ScriptedPeer.Tag(await peer.RequestAsync(0)), "no such file"));
        Task<byte[]> mistaken = StatAsync(connection, 1);
        peer.Reply(new Rclunk(ScriptedPeer.Tag(await peer.RequestAsync(1))));

        Assert.Equal("no such file", (await Assert.ThrowsAsync<MountErrorException>(() => refused)).Message);
        Assert.Equal(MountConnection.RpcError, (await Assert.ThrowsAsync<IOException>(() => mistaken)).Message);
    }

    [Fact]
    public async Task EachReplyGoesToItsCallerWhateverItsOrderAndAReplyToNoOneIsDropped()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        Task<byte[]>[] calls = [StatAsync(connection, 1), StatAsync(connection, 2), StatAsync(connection, 3)];
        byte[][] requests = await peer.RequestsAsync(3);
        peer.Send(StatReply(0x7777, "stray"));
        foreach (byte[] request in requests.Reverse())
        {
            peer.Send(StatReply(ScriptedPeer.Tag(request), $"f{new Tstat(request).Fid}"));
        }

        string[] names = [.. (await Task.WhenAll(calls).WaitAsync(TimeSpan.FromMilliseconds(Bound))).Select(reply => new Rstat(reply).Stat.Name)];
        Assert.Equal(["f1", "f2", "f3"], names);
    }

    [Fact]
    public async Task WhenTheReaderHasItsReplyTheNextCallerStillWaitingReads()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        Task<byte[]>[] calls = [StatAsync(connection, 1), StatAsync(connection, 2), StatAsync(connection, 3)];
        byte[][] requests = await peer.RequestsAsync(3);
        foreach (byte[] request in requests)
        {
            peer.Send(StatReply(ScriptedPeer.Tag(request)));
            await calls[Array.IndexOf(requests, request)].WaitAsync(TimeSpan.FromMilliseconds(Bound));
        }
    }

    [Fact]
    public async Task ACancelledCallFlushesItsRequestAndHasTheReplyIfItCameFirst()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        using var cancel = new CancellationTokenSource();
        Task<byte[]> call = StatAsync(connection, 1, cancel.Token);
        ushort tag = ScriptedPeer.Tag(await peer.RequestAsync(0));
        await cancel.CancelAsync();

        byte[] flush = await peer.RequestAsync(1);
        Assert.Equal((MessageTypes.Tflush, tag), (ScriptedPeer.Type(flush), new Tflush(flush).OldTag));
        peer.Send(StatReply(tag, "answered"));
        peer.Reply(new Rflush(ScriptedPeer.Tag(flush)));

        Assert.Equal("answered", new Rstat(await call.WaitAsync(TimeSpan.FromMilliseconds(Bound))).Stat.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelledCallWhoseReplyIsAnErrorOrNoneIsCancelledAndTheConnectionGoesOn(bool refused)
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        using var cancel = new CancellationTokenSource();
        Task<byte[]> call = StatAsync(connection, 1, cancel.Token);
        ushort tag = ScriptedPeer.Tag(await peer.RequestAsync(0));
        await cancel.CancelAsync();
        byte[] flush = await peer.RequestAsync(1);
        if (refused)
        {
            peer.Reply(new Rerror(tag, "too late"));
        }

        peer.Reply(new Rflush(ScriptedPeer.Tag(flush)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromMilliseconds(Bound)));

        Task<byte[]> later = StatAsync(connection, 2);
        peer.Send(StatReply(ScriptedPeer.Tag(await peer.RequestAsync(2)), "later"));
        Assert.Equal("later", new Rstat(await later.WaitAsync(TimeSpan.FromMilliseconds(Bound))).Stat.Name);
    }

    [Fact]
    public async Task APeerThatHangsUpFailsEveryCallWaitingAndEveryLaterOneWithoutWritingIt()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        Task<byte[]>[] calls = [StatAsync(connection, 1), StatAsync(connection, 2)];
        await peer.RequestsAsync(2);
        peer.HangUp();

        foreach (Task<byte[]> call in calls)
        {
            Assert.Equal(MountConnection.RpcError, (await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(TimeSpan.FromMilliseconds(Bound)))).Message);
        }

        Assert.Equal(MountConnection.RpcError, (await Assert.ThrowsAsync<IOException>(() => StatAsync(connection, 3))).Message);
        Assert.True(peer.Disposed);
    }

    [Fact]
    public async Task ARequestThatCannotBeWrittenFailsAsTheWriteDid()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        peer.RefuseWrites = true;

        Assert.Equal(PipeQueue.Hungup, (await Assert.ThrowsAsync<IOException>(() => StatAsync(connection, 1))).Message);
    }

    // Every tag comes round again once the others have been used: one whose write failed and one a
    // flush took are each free to be used and answered.
    [Fact]
    public async Task TheTagsOfAFailedWriteAndOfAFlushAreUsedAgain()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        peer.RefuseWrites = true;
        await Assert.ThrowsAsync<IOException>(() => StatAsync(connection, 1));
        peer.RefuseWrites = false;
        using var cancel = new CancellationTokenSource();
        Task<byte[]> flushed = StatAsync(connection, 1, cancel.Token);
        await peer.RequestAsync(0);
        await cancel.CancelAsync();
        byte[] flush = await peer.RequestAsync(1);
        peer.Reply(new Rflush(ScriptedPeer.Tag(flush)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flushed);
        using var stop = new CancellationTokenSource();
        Task answering = AnswerClunksAsync(peer, 2, stop.Token);
        for (int call = 0; call < ushort.MaxValue - 1; call++)
        {
            await connection.RpcAsync(tag => new Tclunk(tag, 0), MessageTypes.Rclunk, default).WaitAsync(KernelDriver.Bound);
        }

        await stop.CancelAsync();
        await answering.ContinueWith(_ => { }, TaskScheduler.Default);
        ushort[] used = [.. (await peer.RequestsAsync(2)).Skip(2).Select(ScriptedPeer.Tag)];

        Assert.Equal((ushort)1, used[^3]);
        Assert.Equal(ScriptedPeer.Tag(flush), used[^1]);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(MountConnection.MaxRpc + 1)]
    public async Task AFrameWhoseLengthIsOutOfBoundsBreaksTheConnection(int length)
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        Task<byte[]> call = StatAsync(connection, 1);
        await peer.RequestAsync(0);
        byte[] frame = new byte[Math.Max(length, 7)];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)length);
        peer.Send(frame);

        Assert.Equal(MountConnection.RpcError, (await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(TimeSpan.FromMilliseconds(Bound)))).Message);
    }

    [Fact]
    public async Task AFrameOfTheLargestLengthIsReadAsAreFramesArrivingInPieces()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        Task<byte[]> read = connection.RpcAsync(tag => new Tread(tag, 1, 0, 32768), MessageTypes.Rread, default).WaitAsync(KernelDriver.Bound);
        ushort tag = ScriptedPeer.Tag(await peer.RequestAsync(0));
        byte[] largest = ScriptedPeer.Encode(new Rread(tag, new byte[MountConnection.MaxRpc - NinePConstants.HeaderSize - 4]));
        peer.Send(largest[..3]);
        peer.Send(largest[3..10]);
        peer.Send(largest[10..]);

        Assert.Equal(MountConnection.MaxRpc, (await read.WaitAsync(TimeSpan.FromMilliseconds(Bound))).Length);
    }

    [Fact]
    public async Task AFrameCutShortByTheEndOfTheConnectionBreaksIt()
    {
        (MountConnection connection, ScriptedPeer peer) = await ConnectAsync();
        Task<byte[]> call = StatAsync(connection, 1);
        ushort tag = ScriptedPeer.Tag(await peer.RequestAsync(0));
        peer.Send(StatReply(tag)[..12]);
        peer.HangUp();

        Assert.Equal(MountConnection.RpcError, (await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(TimeSpan.FromMilliseconds(Bound)))).Message);
    }

    private static async Task AnswerClunksAsync(ScriptedPeer peer, int first, CancellationToken cancellationToken)
    {
        for (int index = first; ; index++)
        {
            byte[] request = await peer.RequestAsync(index).WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);
            peer.Reply(new Rclunk(ScriptedPeer.Tag(request)));
        }
    }

    private static async Task<(MountConnection Connection, ScriptedPeer Peer)> ConnectAsync(uint firstFid = 0)
    {
        await using var peer = new ScriptedPeer();
        var connection = new MountConnection(peer, peer.Handle, peer, () => ScriptedPeer.Context, firstFid);
        await connection.VersionAsync().WaitAsync(KernelDriver.Bound);
        return (connection, peer);
    }

    private static Task<byte[]> StatAsync(MountConnection connection, uint fid, CancellationToken cancellationToken = default)
        => connection.RpcAsync(tag => new Tstat(tag, fid), MessageTypes.Rstat, cancellationToken).WaitAsync(KernelDriver.Bound, CancellationToken.None);

    private static byte[] StatReply(ushort tag, string name = "f")
        => ScriptedPeer.Encode(new Rstat(tag, new Stat(0, 0, 0, new Qid(QidType.QTFILE, 0, 1), 0b110_100_100, 0, 0, 0, name, "glenda", "glenda", "glenda")));
}
