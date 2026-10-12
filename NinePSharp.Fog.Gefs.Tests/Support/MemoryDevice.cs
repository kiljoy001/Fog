namespace NinePSharp.Fog.Gefs.Tests.Support;

// A device in memory, holding only the blocks written to it, that can lose power: after a crash
// point, writes stay readable, as if cached, until it restarts, when they are gone. Its next write,
// its next flush, or both, can be held until a scenario lets them go, and it may be used from many
// tasks at once.
internal sealed class MemoryDevice(long blocks) : Device
{
    private readonly object gate = new();
    private readonly Dictionary<long, byte[]> written = [];
    private readonly Dictionary<long, byte[]> cached = [];
    private readonly Dictionary<string, Holding> holds = [];
    private int? writesLeft;
    private bool failing;

    public override long Size => blocks * Format.BlockSize;

    // Each write and flush, in order.
    public List<string> Trace { get; } = [];

    public int Writes => Trace.Count(e => e == "write");

    // Where each write went, by block, in order.
    public List<long> Written { get; } = [];

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
            Written.Add(offset / Format.BlockSize);
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
        lock (gate)
        {
            holds[operation] = new Holding();
        }
    }

    public void WaitHeld(string operation)
    {
        if (!Of(operation).Held.Wait(Bounded.Limit))
        {
            throw new TimeoutException($"no {operation} reached the device");
        }
    }

    public void Let(string operation) => Of(operation).Let.Set();

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

    private Holding Of(string operation)
    {
        lock (gate)
        {
            return holds[operation];
        }
    }

    private void Hold(string operation)
    {
        Holding? hold;
        lock (gate)
        {
            if (!holds.TryGetValue(operation, out hold) || hold.Taken)
            {
                return;
            }

            hold.Taken = true;
        }

        hold.Held.Set();
        if (!hold.Let.Wait(Bounded.Limit))
        {
            throw new TimeoutException($"the held {operation} was never let go");
        }
    }

    private sealed class Holding
    {
        public ManualResetEventSlim Held { get; } = new();

        public ManualResetEventSlim Let { get; } = new();

        public bool Taken { get; set; }
    }
}
