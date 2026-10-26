using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Constants;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;
using NinePSharp.Server.FileSystem;

namespace NinePSharp.Fog.Kernel.Tests.Support;

// NinePSharp's file server over an in-memory tree, at the far end of a kernel pipe, recording what
// the connection carries: Tversions, Tflushes, the largest data in a read or write, and live fids.
public sealed class FileServerHarness : INinePFSDispatcher, INinePSessionLifecycle, IAsyncDisposable
{
    private readonly NinePFSDispatcher server;
    private readonly ConcurrentDictionary<uint, byte> fids = new();
    private readonly ConcurrentDictionary<ushort, CancellationTokenSource> held = new();
    private readonly ConcurrentDictionary<uint, string> opened = new();
    private readonly CancellationTokenSource hangup = new();
    private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TaskCompletionSource<bool>> requestsHeld = [];
    private readonly TaskCompletionSource<bool> holdFlushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int heldRequests;
    private Task serving = Task.CompletedTask;
    private Stream? stream;

    public FileServerHarness()
    {
        server = new NinePFSDispatcher(NullLogger<NinePFSDispatcher>.Instance, new FileSystemBackend(Root));
    }

    public NinePDir Root { get; } = new("/");

    public int Versions { get; private set; }

    public int LargestData { get; private set; }

    public ConcurrentBag<ushort> Flushed { get; } = [];

    public ConcurrentBag<ushort> ReadTags { get; } = [];

    // Each attach's user and tree.
    public ConcurrentQueue<(string User, string Name)> Attaches { get; } = [];

    public int LiveFids => fids.Count;

    public bool HoldReads { get; set; }

    public bool HoldWrites { get; set; }

    // Done once a flush has ended a request held.
    public Task HoldFlushed => holdFlushed.Task;

    // What the server answers of an open's iounit, how much of a read it gives and of a write it
    // takes, and why it refuses Tversion; null leaves the server's own answer.
    public uint? IoUnit { get; set; }

    public int? ReadLimit { get; set; }

    public int? WriteLimit { get; set; }

    public string? VersionRefusal { get; set; }

    public int WStats { get; private set; }

    public ConcurrentQueue<byte> OpenModes { get; } = [];

    // The number of names each Twalk carried, in order.
    public ConcurrentQueue<int> Walks { get; } = [];

    // Every read is refused with this error.
    public string? ReadRefusal { get; set; }

    // An open of this file is refused with this error.
    public (string File, string Error)? Refusal { get; set; }

    // Done once the connection has ended, as the server sees its end of file.
    public Task Ended => serving;

    public void Serve(Process process, int fd)
    {
        stream = new DescriptorStream(process, fd);
        var processor = new NinePConnectionProcessor(NullLogger.Instance, this);
        serving = processor.ProcessStreamAsync(stream, null, new NinePConnectionProcessor.ClientSession(), hangup.Token);
    }

    public void Release() => release.TrySetResult(true);

    // Done once the nth read or write, counting from 1, has come to be held.
    public Task RequestHeld(int n)
    {
        lock (requestsHeld)
        {
            return Held(n).Task;
        }
    }

    // Closing its end hangs the connection up; the server then stops reading.
    public async Task HangUpAsync(Process process, int fd)
    {
        await process.CloseAsync(fd);
        await hangup.CancelAsync();
        foreach (CancellationTokenSource cancel in held.Values)
        {
            await cancel.CancelAsync();
        }

        try
        {
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
    {
        switch (message)
        {
            case NinePMessage.MsgTversion version:
                Versions++;
                if (VersionRefusal is { } why)
                {
                    return new Rerror(version.Item.Tag, why);
                }

                break;
            case NinePMessage.MsgTwstat:
                WStats++;
                break;
            case NinePMessage.MsgTwalk walked:
                Walks.Enqueue(walked.Item.Wname.Length);
                break;
            case NinePMessage.MsgTattach attach:
                Attaches.Enqueue((attach.Item.Uname, attach.Item.Aname));
                break;
            case NinePMessage.MsgTflush flush:
                Flushed.Add(flush.Item.OldTag);
                if (held.TryGetValue(flush.Item.OldTag, out CancellationTokenSource? holding))
                {
                    await holding.CancelAsync();
                }

                break;
            case NinePMessage.MsgTwrite write:
                LargestData = Math.Max(LargestData, (int)write.Item.Count);
                if (HoldWrites && await HoldAsync(write.Item.Tag) is { } refused)
                {
                    return refused;
                }

                break;
            case NinePMessage.MsgTopen open:
                OpenModes.Enqueue(open.Item.Mode);
                if (Refusal is { } refusal && opened.TryGetValue(open.Item.Fid, out string? name) && name == refusal.File)
                {
                    return new Rerror(open.Item.Tag, refusal.Error);
                }

                break;
            case NinePMessage.MsgTread read:
                ReadTags.Add(read.Item.Tag);
                LargestData = Math.Max(LargestData, (int)read.Item.Count);
                if (ReadRefusal is { } error)
                {
                    return new Rerror(read.Item.Tag, error);
                }

                if (HoldReads && await HoldAsync(read.Item.Tag) is { } interrupted)
                {
                    return interrupted;
                }

                break;
        }

        object reply = await server.DispatchAsync(sessionId, message, dialect, certificate);
        Track(message, reply);
        return reply switch
        {
            Ropen open when IoUnit is { } unit => new Ropen(open.Tag, open.Qid, unit),
            Rcreate created when IoUnit is { } unit => new Rcreate(created.Tag, created.Qid, unit),
            Rread read when ReadLimit is { } limit && read.Count > limit => new Rread(read.Tag, read.Data[..limit]),
            Rwrite wrote when WriteLimit is { } limit && wrote.Count > limit => new Rwrite(wrote.Tag, (uint)limit),
            _ => reply,
        };
    }

    public Task CloseSessionAsync(string sessionId) => server.CloseSessionAsync(sessionId);

    public async ValueTask DisposeAsync()
    {
        hangup.Dispose();
        if (stream is not null)
        {
            await stream.DisposeAsync();
        }
    }

    private TaskCompletionSource<bool> Held(int n)
    {
        while (requestsHeld.Count < n)
        {
            requestsHeld.Add(new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return requestsHeld[n - 1];
    }

    private async Task<object?> HoldAsync(ushort tag)
    {
        using var cancel = new CancellationTokenSource();
        held[tag] = cancel;
        lock (requestsHeld)
        {
            Held(++heldRequests).TrySetResult(true);
        }

        try
        {
            await release.Task.WaitAsync(cancel.Token);
            return null;
        }
        catch (OperationCanceledException)
        {
            holdFlushed.TrySetResult(true);
            return new Rerror(tag, "interrupted");
        }
        finally
        {
            held.TryRemove(tag, out _);
        }
    }

    private void Track(NinePMessage message, object reply)
    {
        switch (message)
        {
            case NinePMessage.MsgTattach attach when reply is Rattach:
                fids[attach.Item.Fid] = 0;
                break;
            case NinePMessage.MsgTwalk walk when reply is Rwalk rwalk && rwalk.Wqid.Length == walk.Item.Wname.Length:
                fids[walk.Item.NewFid] = 0;
                opened[walk.Item.NewFid] = walk.Item.Wname.Length > 0 ? walk.Item.Wname[^1] : opened.GetValueOrDefault(walk.Item.Fid, string.Empty);
                break;
            case NinePMessage.MsgTclunk clunk:
                fids.TryRemove(clunk.Item.Fid, out _);
                break;
            case NinePMessage.MsgTremove remove:
                fids.TryRemove(remove.Item.Fid, out _);
                break;
        }
    }
}
