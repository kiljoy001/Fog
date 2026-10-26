using System.Text;

namespace NinePSharp.Fog.Kernel;

// The host's console, which #c/cons writes to as devcons's putstrn writes to the screen and reads as
// drawterm's devcons reads its standard input when it has no screen: Fog, a program on its host,
// writes to its standard error and reads its standard input. setterm makes the host's terminal raw,
// a key at a time without its echo, as drawterm's does; input that is not a terminal is read as it
// comes either way.
public class HostConsole
{
    private readonly object gate = new();
    private readonly Func<TextReader> input;
    private readonly Func<bool> terminal;
    private readonly Func<ConsoleKeyInfo> key;
    private readonly Encoder encoder = Encoding.UTF8.GetEncoder();
    private readonly Queue<byte> pending = new();
    private volatile bool raw;

    public HostConsole()

        // Stryker disable once Boolean : a test's input is never a terminal, so neither the terminal it is not nor its keys without echo can be told apart by one
        : this(() => Console.In, () => !Console.IsInputRedirected, () => Console.ReadKey(intercept: true))
    {
    }

    // The host's standard input, whether it is a terminal, and a key read from the terminal without
    // its echo.
    protected HostConsole(Func<TextReader> input, Func<bool> terminal, Func<ConsoleKeyInfo> key)
        => (this.input, this.terminal, this.key) = (input, terminal, key);

    public static HostConsole System { get; } = new();

    public virtual void Write(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            Console.Error.Write(Encoding.UTF8.GetString(data));
            Console.Error.Flush();
        }
    }

    // setterm: raw, or cooked again.
    public virtual void SetTerm(bool raw) => this.raw = raw;

    // What is typed, at most as much as buffer holds, or nothing once the input has ended; half of a
    // surrogate pair waits for the other.
    public virtual async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (pending.Count == 0)
        {
            char[] chars;
            if (raw && terminal())
            {
                chars = [(await Task.Run(key, cancellationToken).WaitAsync(cancellationToken)).KeyChar];
            }
            else
            {
                chars = new char[buffer.Length];
                TextReader reader = input();
                int n = await Task.Run(() => reader.Read(chars, 0, chars.Length), cancellationToken).WaitAsync(cancellationToken);
                if (n == 0)
                {
                    return 0;
                }

                chars = chars[..n];
            }

            byte[] bytes = new byte[encoder.GetByteCount(chars, flush: false)];
            encoder.GetBytes(chars, bytes, flush: false);
            foreach (byte b in bytes)
            {
                pending.Enqueue(b);
            }
        }

        int count = 0;
        while (count < buffer.Length && pending.TryDequeue(out byte b))
        {
            buffer.Span[count++] = b;
        }

        return count;
    }
}
