using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace NinePSharp.Fog.Gefs;

// syncq and runsync: blocks waiting to be written, a heap ordered by the generation they were
// queued in and then by their place on the device, written by a task of their own that runs while
// anything waits. A fence starts a generation and sorts before its blocks, so it comes out once
// every block queued before it is written. Until it is written a block reads back from the queue.
// Once the device fails a write, or the store fails otherwise, nothing more is written and every
// fence fails, but each block queued still reads back. Unlike gefs, nothing waits on the device
// longer than its patience, for room or for a fence; after that the queue has failed.
internal sealed class SyncQueue(Device device, int room = SyncQueue.Room)
{
    public const int Room = 4096;

    private readonly object gate = new();
    private readonly PriorityQueue<Entry, (long Gen, long Address)> heap = new();
    private readonly ConcurrentDictionary<long, byte[]> queued = new();
    private long gen;
    private bool running;
    private Exception? failure;

    public Exception? Failure => Volatile.Read(ref failure);

    public byte[]? Find(long address) => queued.TryGetValue(address, out byte[]? block) ? block : null;

    // enqueue: a block finished and never changed again, written when its turn comes.
    public void Write(long address, byte[] block)
    {
        queued[address] = block;
        Put(new Entry(address, block, null));
    }

    // wrwait's fence, sorting before every block of its generation.
    public Task Fence()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Put(new Entry(long.MinValue, null, done));
        return done.Task;
    }

    // wrwait: returns once every block queued before it is written.
    public void Wait()
    {
        Task fence = Fence();
        try
        {
            if (fence.Wait(device.Patience))
            {
                return;
            }
        }
        catch (AggregateException failed) when (failed.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Throw(inner);
        }

        throw TimedOut();
    }

    // The first failure stands.
    public void Fail(Exception failed) => Interlocked.CompareExchange(ref failure, failed, null);

    // qput: a full queue waits for the sync task to take something out.
    private void Put(Entry e)
    {
        lock (gate)
        {
            while (heap.Count == room)
            {
                if (!Monitor.Wait(gate, device.Patience))
                {
                    throw TimedOut();
                }
            }

            if (e.Fence is not null)
            {
                gen++;
            }

            heap.Enqueue(e, (gen, e.Address));
            if (!running)
            {
                running = true;
                _ = Task.Run(Run);
            }
        }
    }

    private void Run()
    {
        while (true)
        {
            Entry e;
            lock (gate)
            {
                if (!heap.TryDequeue(out e!, out _))
                {
                    running = false;
                    return;
                }

                Monitor.PulseAll(gate);
            }

            if (e.Fence is { } fence)
            {
                if (Failure is { } failed)
                {
                    fence.SetException(failed);
                }
                else
                {
                    fence.SetResult();
                }
            }
            else if (Failure is null)
            {
                try
                {
                    device.Write(e.Address, e.Block!);
                    queued.TryRemove(new KeyValuePair<long, byte[]>(e.Address, e.Block!));
                }
                catch (Exception failed)
                {
                    Fail(failed);
                }
            }
        }
    }

    private GefsException TimedOut()
    {
        var timedOut = new GefsException("i/o timeout");
        Fail(timedOut);
        return timedOut;
    }

    private sealed record Entry(long Address, byte[]? Block, TaskCompletionSource? Fence);
}
