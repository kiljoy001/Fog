using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Rc.Tests.Support;

// A host console that keeps what is written to it, so the tests neither print it nor lose it, and
// whose input is what the test typed, and then ends.
internal sealed class FakeHostConsole : HostConsole
{
    private readonly StringBuilder text = new();
    private readonly Queue<byte> typed = new();

    public bool Raw { get; private set; }

    public string Text
    {
        get
        {
            lock (text)
            {
                return text.ToString();
            }
        }
    }

    public void Type(string input)
    {
        lock (typed)
        {
            foreach (byte b in Encoding.UTF8.GetBytes(input))
            {
                typed.Enqueue(b);
            }
        }
    }

    public override void SetTerm(bool raw) => Raw = raw;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (typed)
        {
            int n = 0;
            while (n < buffer.Length && typed.TryDequeue(out byte b))
            {
                buffer.Span[n++] = b;
            }

            return ValueTask.FromResult(n);
        }
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (text)
        {
            text.Append(Encoding.UTF8.GetString(data));
        }
    }
}
