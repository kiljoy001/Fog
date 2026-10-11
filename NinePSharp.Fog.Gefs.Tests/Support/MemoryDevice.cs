namespace NinePSharp.Fog.Gefs.Tests.Support;

// A device in memory, holding only the blocks written to it, that can lose power: after a crash
// point, writes stay readable, as if cached, until it restarts, when they are gone. Its next write or
// flush can be held until a scenario lets it go, and it may be used from many tasks at once.
internal sealed class MemoryDevice(long blocks) : Device
{
    private readonly object gate = new();
    private readonly Dictionary<long, byte[]> written = [];
    private readonly Dictionary<long, byte[]> cached = [];
    private int? writesLeft;
    private bool failing;
    private string? holding;
    private ManualResetEventSlim held = new();
    private ManualResetEventSlim let = new();

    public override long Size => blocks * Format.BlockSize;

    // Each write and flush, in order.
    public List<string> Trace { get; } = [];

    public int Writes => Trace.Count(e => e == "write");

    public override void Read(long offset, Span<byte> block)
    {
        lock (gate)
        {
            block.Clear();
            if (cached.TryGetValue(offset, out byte[]? bytes) || written.TryGetValue(offset, out bytes))
            {
                bytes.CopyTo(block);
            }
        }
    }

    public override void Write(long offset, ReadOnlySpan<byte> block)
    {
        Hold("write");
        lock (gate)
        {
            Trace.Add("write");
            if (failing)
            {
                throw new IOException("device write failed");
            }

            if (writesLeft is 0)
            {
                cached[offset] = block.ToArray();
                return;
            }

            writesLeft--;
            written[offset] = block.ToArray();
        }
    }

    public override void Flush()
    {
        Hold("flush");
        lock (gate)
        {
            Trace.Add("flush");
        }
    }

    // The next write or flush waits, once it starts, until Let.
    public void HoldNext(string operation)
    {
        held = new ManualResetEventSlim();
        let = new ManualResetEventSlim();
        holding = operation;
    }

    public void WaitHeld()
    {
        if (!held.Wait(Bounded.Limit))
        {
            throw new TimeoutException($"no {holding} reached the device");
        }
    }

    public void Let() => let.Set();

    public void CrashAfter(int writes) => writesLeft = writes;

    public void FailWrites() => failing = true;

    public void Restart()
    {
        writesLeft = null;
        cached.Clear();
    }

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

    private void Hold(string operation)
    {
        if (Interlocked.CompareExchange(ref holding, null, operation) == operation)
        {
            held.Set();
            if (!let.Wait(Bounded.Limit))
            {
                throw new TimeoutException($"the held {operation} was never let go");
            }
        }
    }
}
