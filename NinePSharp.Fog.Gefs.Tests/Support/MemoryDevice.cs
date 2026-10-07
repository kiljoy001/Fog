namespace NinePSharp.Fog.Gefs.Tests.Support;

// A device in memory that can lose power: after a crash point, writes no longer reach it.
internal sealed class MemoryDevice(long blocks) : Device
{
    private readonly byte[] bytes = new byte[blocks * Format.BlockSize];
    private int? writesLeft;

    public override long Size => bytes.Length;

    public int Writes { get; private set; }

    public override void Read(long offset, Span<byte> block) => bytes.AsSpan((int)offset, block.Length).CopyTo(block);

    public override void Write(long offset, ReadOnlySpan<byte> block)
    {
        Writes++;
        if (writesLeft is 0)
        {
            return;
        }

        writesLeft--;
        block.CopyTo(bytes.AsSpan((int)offset));
    }

    public override void Flush()
    {
    }

    public void CrashAfter(int writes) => writesLeft = writes;

    public void Restart() => writesLeft = null;

    public byte[] Block(long n) => bytes.AsSpan((int)(n * Format.BlockSize), Format.BlockSize).ToArray();

    public void Damage(long n) => bytes[(n * Format.BlockSize) + 100] ^= 0xff;

    public void Put(long n, byte[] block) => block.CopyTo(bytes, n * Format.BlockSize);
}
