namespace NinePSharp.Fog.Gefs.Tests.Support;

// A device in memory, holding only the blocks written to it, that can lose power: after a crash
// point, writes no longer reach it.
internal sealed class MemoryDevice(long blocks) : Device
{
    private readonly Dictionary<long, byte[]> written = [];
    private int? writesLeft;

    public override long Size => blocks * Format.BlockSize;

    // Each write and flush, in order.
    public List<string> Trace { get; } = [];

    public override void Read(long offset, Span<byte> block)
    {
        block.Clear();
        if (written.TryGetValue(offset, out byte[]? bytes))
        {
            bytes.CopyTo(block);
        }
    }

    public override void Write(long offset, ReadOnlySpan<byte> block)
    {
        Trace.Add("write");
        if (writesLeft is 0)
        {
            return;
        }

        writesLeft--;
        written[offset] = block.ToArray();
    }

    public override void Flush() => Trace.Add("flush");

    public void CrashAfter(int writes) => writesLeft = writes;

    public void Restart() => writesLeft = null;

    public byte[] Block(long n)
    {
        var block = new byte[Format.BlockSize];
        Read(n * Format.BlockSize, block);
        return block;
    }

    public void Damage(long n)
    {
        byte[] block = Block(n);
        block[100] ^= 0xff;
        written[n * Format.BlockSize] = block;
    }

    public void Put(long n, byte[] block) => written[n * Format.BlockSize] = block;
}
