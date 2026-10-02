using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;

namespace NinePSharp.Fog.Auth.Tests.Support;

// The export under the afid layer: attaches a directory as the user the afid authenticated, or as
// nobody without an afid, and walks to directories up to one named "missing".
internal sealed class RecordingExport : INinePFSDispatcher, INinePSessionLifecycle
{
    internal AuthFidExport? Auth { get; set; }

    internal List<(uint Fid, string? User)> Attaches { get; } = new();

    internal List<string> Closed { get; } = new();

    public Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
    {
        object reply = message switch
        {
            NinePMessage.MsgTversion m => new Rversion(m.Item.Tag, m.Item.MSize, m.Item.Version),
            NinePMessage.MsgTattach m => Attach(sessionId, m.Item),
            NinePMessage.MsgTwalk m => new Rwalk(m.Item.Tag, m.Item.Wname.TakeWhile(name => name != "missing").Select((_, index) => Directory((ulong)index + 10)).ToArray()),
            NinePMessage.MsgTclunk m => new Rclunk(m.Item.Tag),
            _ => new Rerror(NinePConstants.NoTag, "unexpected"),
        };
        return Task.FromResult(reply);
    }

    public Task CloseSessionAsync(string sessionId)
    {
        Closed.Add(sessionId);
        return Task.CompletedTask;
    }

    private static Qid Directory(ulong path) => new(QidType.QTDIR, 0, path);

    private object Attach(string sessionId, Tattach request)
    {
        string? user = null;
        if (request.Afid != NinePConstants.NoFid && (user = Auth!.AuthenticatedUser(sessionId, request.Afid)) is null)
        {
            return new Rerror(request.Tag, "denied");
        }

        Attaches.Add((request.Fid, user));
        return new Rattach(request.Tag, Directory(1));
    }
}
