using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

// The mount driver's tree over a peer that answers every request as a server holding one file.
public sealed class MountTreeTests
{
    private static readonly Qid Directory = new(QidType.QTDIR, 0, 0);
    private static readonly Qid File = new(QidType.QTFILE, 0, 1);

    [Fact]
    public async Task RemovingAnOpenFileRemovesItThroughTheFidItIsOpenOn()
    {
        await using var peer = new ScriptedPeer();
        var connection = new MountConnection(peer, peer.Handle, peer, () => ScriptedPeer.Context);
        await connection.VersionAsync();
        using var answering = new CancellationTokenSource();
        Task server = AnswerAsync(peer, answering.Token);
        MountTree tree = await MountTree.AttachAsync(connection, "#M0", "glenda", string.Empty, 8);
        ResourceHandle file = (await tree.WalkAsync(tree.Root, "notes", CancellationToken.None))!;
        ResourceOpenHandle open = await tree.OpenAsync(file, NinePConstants.OREAD, CancellationToken.None);
        uint openFid = new Topen((await peer.RequestsAsync(4))[3]).Fid;

        await tree.RemoveAsync(file, open, CancellationToken.None);
        byte[] remove = (await peer.RequestsAsync(5))[4];
        await answering.CancelAsync();
        await server.ContinueWith(_ => { }, TaskScheduler.Default);

        Assert.Equal((MessageTypes.Tremove, openFid), (ScriptedPeer.Type(remove), new Tremove(remove).Fid));
        await Assert.ThrowsAsync<IOException>(() => tree.ReadAsync(open, 0, 1, CancellationToken.None));
    }

    private static async Task AnswerAsync(ScriptedPeer peer, CancellationToken cancellationToken)
    {
        for (int index = 0; ; index++)
        {
            byte[] request = await peer.RequestAsync(index).WaitAsync(cancellationToken);
            ushort tag = ScriptedPeer.Tag(request);
            peer.Reply(ScriptedPeer.Type(request) switch
            {
                MessageTypes.Tattach => new Rattach(tag, Directory),
                MessageTypes.Twalk => new Rwalk(tag, new Twalk(request).Wname.Length == 0 ? [] : [File]),
                MessageTypes.Topen => new Ropen(tag, File, 0),
                MessageTypes.Tremove => new Rremove(tag),
                _ => new Rclunk(tag),
            });
        }
    }
}
