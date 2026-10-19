using System.Collections.Concurrent;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Kernel.Tests.Support;

// A file server behind a scripted peer: a root holding the file "notes" and the directories "a",
// "b" and "c", answering every request it is sent and keeping each in order.
internal sealed class ScriptedServer : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<uint, string> fids = new();
    private readonly Task answering;

    public ScriptedServer()
    {
        answering = AnswerAsync();
    }

    public static byte[] Data { get; } = "0123456789"u8.ToArray();

    public ScriptedPeer Peer { get; } = new();

    public ConcurrentQueue<byte[]> Requests { get; } = [];

    // Each name's qid path, which a test may change, as if the file were replaced.
    public ConcurrentDictionary<string, ulong> Paths { get; } = new(StringComparer.Ordinal)
    {
        ["notes"] = 1,
        ["a"] = 2,
        ["b"] = 3,
        ["c"] = 4,
        ["new"] = 5,
    };

    public bool RefuseCreates { get; set; }

    public uint IoUnit { get; set; }

    // Done once a stat has come to be held.
    public TaskCompletionSource StatHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Stats wait for this, once set.
    public Task? HoldStats { get; set; }

    public IEnumerable<uint> Clunked => Of(MessageTypes.Tclunk).Select(message => new Tclunk(message).Fid);

    public IEnumerable<byte[]> Of(MessageTypes type) => Requests.Where(message => ScriptedPeer.Type(message) == type);

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await answering.ContinueWith(_ => { }, TaskScheduler.Default);
        stopping.Dispose();
        await Peer.DisposeAsync();
    }

    private static Qid QidOf(string name, ulong path) => new(name is "notes" or "new" ? QidType.QTFILE : QidType.QTDIR, 0, path);

    private static byte[] Record(string name, Qid qid)
    {
        var stat = new Stat((ushort)(49 + name.Length + 18), 0, 0, qid, 0b110_100_100, 0, 0, 0, name, "glenda", "glenda", "glenda");
        return KernelFileStats.Encode(stat);
    }

    private async Task AnswerAsync()
    {
        for (int index = 0; ; index++)
        {
            byte[] request = await Peer.RequestAsync(index).WaitAsync(Timeout.InfiniteTimeSpan, stopping.Token);
            Requests.Enqueue(request);
            if (ScriptedPeer.Type(request) == MessageTypes.Tstat && HoldStats is { } hold)
            {
                _ = hold.ContinueWith(_ => Peer.Reply(Answer(request)), TaskScheduler.Default);
                StatHeld.TrySetResult();
                continue;
            }

            Peer.Reply(Answer(request));
        }
    }

    // A fid it does not know is refused, as a server refuses one.
    private ISerializable Answer(byte[] request)
    {
        try
        {
            return Serve(request);
        }
        catch (KeyNotFoundException)
        {
            return new Rerror(ScriptedPeer.Tag(request), "unknown fid");
        }
    }

    private ISerializable Serve(byte[] request)
    {
        ushort tag = ScriptedPeer.Tag(request);
        switch (ScriptedPeer.Type(request))
        {
            case MessageTypes.Tattach:
                fids[new Tattach(request).Fid] = string.Empty;
                return new Rattach(tag, new Qid(QidType.QTDIR, 0, 0));
            case MessageTypes.Twalk:
                var walk = new Twalk(request);
                string at = fids[walk.Fid];
                var qids = new List<Qid>();
                foreach (string name in walk.Wname)
                {
                    if (at.Length != 0 || !Paths.TryGetValue(name, out ulong path))
                    {
                        break;
                    }

                    at = name;
                    qids.Add(QidOf(name, path));
                }

                if (qids.Count == walk.Wname.Length)
                {
                    fids[walk.NewFid] = at;
                }

                return qids.Count == 0 && walk.Wname.Length != 0 ? new Rerror(tag, "file does not exist") : new Rwalk(tag, [.. qids]);
            case MessageTypes.Topen:
                string opened = fids[new Topen(request).Fid];
                return new Ropen(tag, opened.Length == 0 ? new Qid(QidType.QTDIR, 0, 0) : QidOf(opened, Paths[opened]), IoUnit);
            case MessageTypes.Tcreate:
                var create = new Tcreate(request);
                if (RefuseCreates)
                {
                    return new Rerror(tag, "create prohibited");
                }

                fids[create.Fid] = create.Name;
                return new Rcreate(tag, QidOf(create.Name, Paths[create.Name]), 0);
            case MessageTypes.Tread:
                var read = new Tread(request);
                string file = fids[read.Fid];
                byte[] content = file.Length == 0
                    ? [.. new[] { "notes", "a", "b", "c" }.SelectMany(name => Record(name, QidOf(name, Paths[name])))]
                    : Data;
                int from = (int)Math.Min(read.Offset, (ulong)content.Length);
                return new Rread(tag, content.AsMemory(from, (int)Math.Min(read.Count, (uint)(content.Length - from))));
            case MessageTypes.Twrite:
                return new Rwrite(tag, new Twrite(request).Count);
            case MessageTypes.Tstat:
                string stated = fids[new Tstat(request).Fid];
                return new Rstat(tag, new Stat(0, 0, 0, QidOf(stated, stated.Length == 0 ? 0 : Paths[stated]), 0b110_100_100, 0, 0, 0, stated, "glenda", "glenda", "glenda"));
            case MessageTypes.Tremove:
                fids.TryRemove(new Tremove(request).Fid, out _);
                return new Rremove(tag);
            default:
                fids.TryRemove(new Tclunk(request).Fid, out _);
                return new Rclunk(tag);
        }
    }
}
