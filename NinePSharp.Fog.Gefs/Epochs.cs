namespace NinePSharp.Fog.Gefs;

// blk.c's epochs and limbo. A read runs inside the epoch current when it starts. A freed block waits
// in the current epoch's limbo, and goes back to the arenas only after the epoch has moved on twice,
// which it does only when no read is still inside an earlier one. gefs keeps an epoch for each worker
// process; Fog, whose reads run on any thread, counts the reads inside each of the three instead.
internal sealed class Epochs
{
    private readonly int[] active = new int[3];
    private readonly List<long>[] limbo = [[], [], []];
    private int epoch;

    // The blocks waiting in limbo.
    public IEnumerable<long> Waiting => limbo.SelectMany(l => l);

    // epochstart: inside the current epoch, retrying if it moved on before the read was counted.
    public int Start()
    {
        while (true)
        {
            int e = Volatile.Read(ref epoch);
            Interlocked.Increment(ref active[e]);
            if (Volatile.Read(ref epoch) == e)
            {
                return e;
            }

            Interlocked.Decrement(ref active[e]);
        }
    }

    // epochend
    public void End(int e) => Interlocked.Decrement(ref active[e]);

    // limbo, by the writer only.
    public void Defer(long block) => limbo[epoch].Add(block);

    // epochclean, by the writer only: the epoch moves on while every read is inside the current one,
    // and each move frees the limbo of the epoch before the previous one. Three moves free it all.
    public List<long> Clean()
    {
        var freed = new List<long>();
        for (int moves = 0; moves < 3; moves++)
        {
            int ge = epoch;
            if (Volatile.Read(ref active[(ge + 1) % 3]) != 0 || Volatile.Read(ref active[(ge + 2) % 3]) != 0)
            {
                break;
            }

            freed.AddRange(limbo[(ge + 1) % 3]);
            limbo[(ge + 1) % 3].Clear();
            Volatile.Write(ref epoch, (ge + 1) % 3);
        }

        return freed;
    }
}
