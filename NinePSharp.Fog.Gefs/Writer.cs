namespace NinePSharp.Fog.Gefs;

// runmutate: changes run one at a time, in the order they were asked for, each under the writer's
// lock, and after each, what waits in limbo goes back once its reads have finished, as epochclean
// does. The task runs while changes wait for it.
internal sealed class Writer(object gate, Allocator allocator)
{
    private readonly Queue<Action> changes = [];
    private bool running;

    public Task Run(Action change) => Run(() =>
    {
        change();
        return true;
    });

    public Task<T> Run<T>(Func<T> change)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (changes)
        {
            changes.Enqueue(() =>
            {
                try
                {
                    done.SetResult(change());
                }
                catch (Exception failed)
                {
                    done.SetException(failed);
                }
            });
            if (!running)
            {
                running = true;
                _ = Task.Run(Drain);
            }
        }

        return done.Task;
    }

    private void Drain()
    {
        while (true)
        {
            Action next;
            lock (changes)
            {
                if (!changes.TryDequeue(out next!))
                {
                    running = false;
                    return;
                }
            }

            lock (gate)
            {
                next();
                allocator.Clean();
            }
        }
    }
}
