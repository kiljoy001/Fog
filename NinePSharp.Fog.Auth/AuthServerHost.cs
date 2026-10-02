using System.Net;
using System.Net.Sockets;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// The auth service of 9front's authsrv run with -N: AuthPAK and form 1 ticket requests against the
/// keys a <see cref="KeyFsHost"/> holds. Disposing stops listening and closes every connection.
/// </summary>
public sealed class AuthServerHost : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly AuthServerOptions options;
    private readonly KeyFsHost keys;
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> connections = new();
    private readonly Task accepting;

    private AuthServerHost(TcpListener listener, AuthServerOptions options, KeyFsHost keys)
    {
        this.listener = listener;
        this.options = options;
        this.keys = keys;
        LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
        accepting = AcceptAsync();
    }

    public IPEndPoint LocalEndPoint { get; }

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

    public static AuthServerHost Start(AuthServerOptions options, KeyFsHost keys)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);
        var listener = new TcpListener(options.EndPoint);
        listener.Start();
        return new AuthServerHost(listener, options, keys);
    }

    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource stop = stopping;
        await Task.WhenAll(await StopAcceptingAsync());
    }

    private async Task<Task[]> StopAcceptingAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
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
                TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token);
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
            // The server is shutting down.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            // authsrv's alarm bounds the whole connection, not each read.
            lifetime.CancelAfter(options.ConnectionLifetime);
            try
            {
                await new AuthServerConnection(client.GetStream(), keys.FindKey, options.SpeaksFor).ServeAsync(lifetime.Token);
            }
            catch (Exception)
            {
                // Any failure ends only this connection.
            }
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
