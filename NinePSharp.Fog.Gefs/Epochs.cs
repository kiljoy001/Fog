namespace NinePSharp.Fog.Gefs;

// blk.c's epochs and limbo. A read runs inside the epoch current when it starts, and takes the root
// it reads only after that, so a block freed before then is not in its tree. A freed block waits in
// the current epoch's limbo, and goes back to the arenas only after the epoch has moved on twice,
// which it does only when no read is inside an earlier one. gefs keeps an epoch for each worker
// process; Fog, whose reads run on any thread, counts the reads inside each of the three instead.
// With no read running at all, nothing in limbo can be in use, and it all goes back at once.
internal sealed class Epochs
{
    private readonly int[] active = new int[3];
    private readonly List<long>[] limbo = [[], [], []];
    private int epoch;

    // The blocks waiting in limbo.
    public IEnumerable<long> Waiting => limbo.SelectMany(l => l);

    // epochstart: a read counted in the epoch it saw, which holds back any later move even if the
    // epoch moved on as it was counted.
    public int Start()
    {
        int e = Volatile.Read(ref epoch);
        Interlocked.Increment(ref active[e]);
        return e;
    }

    // epochend
    public void End(int e) => Interlocked.Decrement(ref active[e]);

    // limbo, by the writer only.
    public void Defer(long block) => limbo[epoch].Add(block);

    // epochclean, by the writer only.
    public List<long> Clean()
    {
        int ge = epoch;
        if (Volatile.Read(ref active[0]) == 0 && Volatile.Read(ref active[1]) == 0 && Volatile.Read(ref active[2]) == 0)
        {
            List<long> all = [.. Waiting];
            Array.ForEach(limbo, l => l.Clear());
            return all;
        }

        if (Volatile.Read(ref active[(ge + 1) % 3]) != 0 || Volatile.Read(ref active[(ge + 2) % 3]) != 0)
        {
            return [];
        }

        List<long> freed = [.. limbo[(ge + 1) % 3]];
        limbo[(ge + 1) % 3].Clear();
        Volatile.Write(ref epoch, (ge + 1) % 3);
        return freed;
    }
}
