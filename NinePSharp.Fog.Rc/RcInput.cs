namespace NinePSharp.Fog.Rc;

// Bytes for the lexer, as rc's io read with rchr: each byte, then -1 at the end.
internal sealed class RcInput(Stream stream)
{
    internal static RcInput FromBytes(byte[] bytes) => new(new MemoryStream(bytes));

    internal int Read() => stream.ReadByte();
}
