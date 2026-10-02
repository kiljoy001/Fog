using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Interfaces;
using NinePSharp.Server;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// Serves a dispatcher on an owner-only Unix-domain socket and nowhere else. A socket left by an
/// earlier host is replaced. Disposing stops accepting, closes every connection, waits for them to
/// finish and removes the socket.
/// </summary>
internal sealed class KeyFsAdminListener : IAsyncDisposable
{
    private readonly Socket socket;
    private readonly NinePConnectionProcessor processor;
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> connections = new();
    private readonly Task accepting;

    private KeyFsAdminListener(Socket socket, INinePFSDispatcher dispatcher)
    {
        this.socket = socket;
        processor = new NinePConnectionProcessor(NullLogger.Instance, dispatcher);
        accepting = AcceptAsync();
    }

    internal int ConnectionCount
    {
        get
        {
            lock (connections)
            {
                return connections.Count;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource stop = stopping;
        await Task.WhenAll(await StopAcceptingAsync());
    }

    internal static KeyFsAdminListener Start(string path, INinePFSDispatcher dispatcher)
    {
        if (Path.Exists(path))
        {
            if (Directory.Exists(path))
            {
                throw new KeyFsException($"keyfs: {path} is a directory");
            }

            if (IsServed(path))
            {
                throw new KeyFsException($"keyfs: another keyfs is serving {path}");
            }

            // Left by a host that did not shut down; the path is inside the keyfs's own state directory.
            File.Delete(path);
        }

        return new KeyFsAdminListener(Listen(path), dispatcher);
    }

    private static Socket Listen(string path)
    {
        var candidate = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            candidate.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            candidate.Listen();
            return candidate;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException or IOException)
        {
            using (candidate)
            {
                throw new KeyFsException($"keyfs: cannot listen on {path}", exception);
            }
        }
    }

    private static bool IsServed(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task<Task[]> StopAcceptingAsync()
    {
        await stopping.CancelAsync();

        // Closing the socket ends the accept loop; disposing it also removes its file.
        socket.Dispose();
        lock (connections)
        {
            return [accepting, .. connections];
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                Socket client = await socket.AcceptAsync(stopping.Token);
                Task connection = ServeAsync(client);
                lock (connections)
                {
                    connections.Add(connection);
                }

                _ = connection.ContinueWith(Forget, TaskScheduler.Default);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The listener is shutting down.
        }
    }

    private async Task ServeAsync(Socket client)
    {
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);
            await processor.ProcessStreamAsync(stream, client.LocalEndPoint, new NinePConnectionProcessor.ClientSession(), stopping.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The client went away or the listener is shutting down.
        }
    }

    private void Forget(Task connection)
    {
        lock (connections)
        {
            connections.Remove(connection);
        }
    }
}
