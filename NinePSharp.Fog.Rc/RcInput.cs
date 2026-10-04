namespace NinePSharp.Fog.Rc;

// Bytes for the lexer, as rc's io read with rchr: each byte, then -1 at the end.
internal sealed class RcInput(Func<ValueTask<int>> read)
{
    internal static RcInput FromBytes(byte[] bytes)
    {
        int next = 0;
        return new(() => ValueTask.FromResult(next < bytes.Length ? bytes[next++] : -1));
    }

    internal ValueTask<int> ReadAsync() => read();
}
