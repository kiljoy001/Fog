using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace NinePSharp.Fog.Server;

/// <summary>
/// Accepts 9P connections up to a limit, gives each a bounded lifetime, and on disposal stops
/// accepting and waits for its connections to end: each has the drain limit to finish its requests,
/// and the listener waits as long again before force-closing it, leaving its outcome unknown.
/// Subclasses serve one connection. A connection that fails other than by its transport ending is
/// logged.
/// </summary>
public abstract class FogListener : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly ILogger logger;
    private readonly int maximumConnections;
    private readonly TimeSpan sessionLifetime;
    private readonly TimeSpan drain;
    private readonly ConcurrentDictionary<TcpClient, Task> connections = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly object lifecycleGate = new();
    private Task? accepting;
    private Task? disposal;

    private protected FogListener(IPEndPoint endpoint, ILogger logger, int maximumConnections, TimeSpan sessionLifetime, TimeSpan? drain)
    {
        this.drain = drain ?? TimeSpan.FromSeconds(5);
        this.logger = logger;
        this.maximumConnections = maximumConnections;
        this.sessionLifetime = sessionLifetime;
        listener = new TcpListener(endpoint);
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)listener.LocalEndpoint;

    // How long a connection that has ended waits for its requests; the listener waits twice that on disposal.
    private protected TimeSpan Drain => drain;

    public void Start()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (accepting is not null)
            {
                throw new InvalidOperationException("Listener already started.");
            }

            listener.Start();
            accepting = AcceptAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate)
        {
            return new ValueTask(disposal ??= DisposeCoreAsync());
        }
    }

    internal static TcpClient ConfigureAcceptedClient(TcpClient client)
    {
        client.NoDelay = true;
        return client;
    }

    private protected abstract Task ServeAsync(TcpClient client, CancellationToken lifetime);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = ConfigureAcceptedClient(await listener.AcceptTcpClientAsync(stopping.Token));
                if (connections.Count >= maximumConnections)
                {
                    client.Dispose();
                }
                else
                {
                    var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    connections.TryAdd(client, finished.Task);
                    _ = ServeConnectionAsync(client, finished);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }

        // Disposal between accepts: a stopped TcpListener rejects the next accept before observing the token.
        catch (InvalidOperationException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, TaskCompletionSource finished)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            lifetime.CancelAfter(sessionLifetime);
            await ServeAsync(client, lifetime.Token);
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // No unauthenticated request, certificate data, proof or private exception is sent on the wire.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A 9P connection failed.");
        }
        finally
        {
            client.Dispose();
            connections.TryRemove(client, out _);
            finished.TrySetResult();
        }
    }

    private async Task DisposeCoreAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();

        // The accept loop must end before the snapshot: a client accepted during disposal is still awaited,
        // and the loop never reads the token of a disposed source.
        if (accepting is not null)
        {
            await accepting;
        }

        try
        {
            await Task.WhenAll(connections.Values).WaitAsync(2 * drain);
        }
        catch (TimeoutException)
        {
            TcpClient[] unfinished = connections.Keys.ToArray();
            foreach (TcpClient client in unfinished)
            {
                client.Dispose();
            }

            logger.LogWarning("{Count} connections had not finished and were force-closed; their outcome is unknown.", unfinished.Length);
        }

        stopping.Dispose();
    }
}
