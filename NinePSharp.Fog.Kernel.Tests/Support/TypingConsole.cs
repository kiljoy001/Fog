using System.Text;

namespace NinePSharp.Fog.Kernel.Tests.Support;

// A host console typed on by the test: each read gives as much of what has been typed as it asks
// for, waiting until something is, or nothing once the input has ended.
public sealed class TypingConsole : HostConsole
{
    private readonly Queue<byte> typed = new();
    private readonly SemaphoreSlim available = new(0);
    private readonly StringBuilder written = new();
    private bool ended;

    public bool Raw { get; private set; }

    // How much of what was typed is still to be read.
    public int Unread
    {
        get
        {
            lock (typed)
            {
                return typed.Count;
            }
        }
    }

    public string Written
    {
        get
        {
            lock (written)
            {
                return written.ToString();
            }
        }
    }

    public void Type(byte[] bytes)
    {
        lock (typed)
        {
            foreach (byte b in bytes)
            {
                typed.Enqueue(b);
            }
        }

        available.Release();
    }

    public void Type(string text) => Type(Encoding.UTF8.GetBytes(text));

    public void End()
    {
        lock (typed)
        {
            ended = true;
        }

        available.Release();
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (written)
        {
            written.Append(Encoding.UTF8.GetString(data));
        }
    }

    public override void SetTerm(bool raw) => Raw = raw;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (typed)
            {
                if (typed.Count > 0 || ended)
                {
                    int n = 0;
                    while (n < buffer.Length && typed.TryDequeue(out byte b))
                    {
                        buffer.Span[n++] = b;
                    }

                    if (typed.Count > 0 || ended)
                    {
                        available.Release();
                    }

                    return n;
                }
            }

            await available.WaitAsync(cancellationToken);
        }
    }
}
