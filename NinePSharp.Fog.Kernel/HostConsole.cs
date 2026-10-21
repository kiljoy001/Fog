using System.Text;

namespace NinePSharp.Fog.Kernel;

// The host's console, which #c/cons writes to as devcons's putstrn writes to the screen: Fog, a
// program on its host, writes to its standard error.
public class HostConsole
{
    private readonly object gate = new();

    public static HostConsole System { get; } = new();

    public virtual void Write(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            Console.Error.Write(Encoding.UTF8.GetString(data));
            Console.Error.Flush();
        }
    }
}
