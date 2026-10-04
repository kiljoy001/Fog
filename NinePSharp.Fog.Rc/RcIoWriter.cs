using System.Text;

namespace NinePSharp.Fog.Rc;

// The lexer's error messages, written into an io with the rest of what goes to it.
internal sealed class RcIoWriter(RcIo io) : TextWriter
{
    public override Encoding Encoding => Encoding.Latin1;

    public override void Write(char value) => io.Pchr(value);
}
