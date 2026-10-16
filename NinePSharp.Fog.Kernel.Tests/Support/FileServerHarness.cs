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

    public int LiveFids => fids.Count;

    public bool HoldReads { get; set; }

    // An open of this file is refused with this error.
    public (string File, string Error)? Refusal { get; set; }

    public void Serve(Process process, int fd)
    {
        stream = new DescriptorStream(process, fd);
        var processor = new NinePConnectionProcessor(NullLogger.Instance, this);
        serving = processor.ProcessStreamAsync(stream, null, new NinePConnectionProcessor.ClientSession(), hangup.Token);
    }

    public void Release() => release.TrySetResult(true);

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
            case NinePMessage.MsgTversion:
                Versions++;
                break;
            case NinePMessage.MsgTflush flush:
                Flushed.Add(flush.Item.OldTag);
                if (held.TryRemove(flush.Item.OldTag, out CancellationTokenSource? cancel))
                {
                    await cancel.CancelAsync();
                }

                break;
            case NinePMessage.MsgTwrite write:
                LargestData = Math.Max(LargestData, (int)write.Item.Count);
                break;
            case NinePMessage.MsgTopen open when Refusal is { } refusal && opened.TryGetValue(open.Item.Fid, out string? name) && name == refusal.File:
                return new Rerror(open.Item.Tag, refusal.Error);
            case NinePMessage.MsgTread read:
                ReadTags.Add(read.Item.Tag);
                LargestData = Math.Max(LargestData, (int)read.Item.Count);
                if (HoldReads && await HoldAsync(read.Item) is { } interrupted)
                {
                    return interrupted;
                }

                break;
        }

        object reply = await server.DispatchAsync(sessionId, message, dialect, certificate);
        Track(message, reply);
        return reply;
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

    private async Task<object?> HoldAsync(Tread read)
    {
        using var cancel = new CancellationTokenSource();
        held[read.Tag] = cancel;
        try
        {
            await release.Task.WaitAsync(cancel.Token);
            return null;
        }
        catch (OperationCanceledException)
        {
            return new Rerror(read.Tag, "interrupted");
        }
        finally
        {
            held.TryRemove(read.Tag, out _);
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
