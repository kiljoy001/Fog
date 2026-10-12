namespace NinePSharp.Fog.Gefs;

// runmutate: changes run one at a time, in the order they were asked for, each under the writer's
// lock, and after each, what waits in limbo goes back once its reads have finished, as epochclean
// does. The task runs while changes wait for it; the change it is running stays at the head of the
// queue, so one asked for meanwhile finds it busy and waits its turn.
internal sealed class Writer(object gate, Allocator allocator)
{
    private readonly Queue<Action> changes = [];

    public async Task<T> Run<T>(Func<T> change)
    {
        T result = default!;
        await Run(() =>
        {
            result = change();
        });
        return result;
    }

    public Task Run(Action change)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (changes)
        {
            changes.Enqueue(() =>
            {
                try
                {
                    change();
                    done.SetResult();
                }
                catch (Exception failed)
                {
                    done.SetException(failed);
                }
            });
            if (changes.Count == 1)
            {
                _ = Task.Run(Drain);
            }
        }

        return done.Task;
    }

    private void Drain()
    {
        Action next;
        lock (changes)
        {
            next = changes.Peek();
        }

        while (true)
        {
            lock (gate)
            {
                next();
                allocator.Clean();
            }

            lock (changes)
            {
                changes.Dequeue();
                if (!changes.TryPeek(out next!))
                {
                    return;
                }
            }
        }
    }
}
