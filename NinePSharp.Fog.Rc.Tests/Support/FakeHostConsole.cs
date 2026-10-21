using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Rc.Tests.Support;

// A host console that keeps what is written to it, so the tests neither print it nor lose it.
internal sealed class FakeHostConsole : HostConsole
{
    private readonly StringBuilder text = new();

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

    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (text)
        {
            text.Append(Encoding.UTF8.GetString(data));
        }
    }
}
