namespace NinePSharp.Fog.Gefs;

// runmutate: changes run one at a time, in the order they were asked for, each under the writer's
// lock, and after each, what waits in limbo goes back once its reads have finished, as epochclean
// does. The task runs while changes wait for it; the change it is running stays at the head of the
// queue, so one asked for meanwhile finds it busy and waits its turn. A change is a task of its own,
// run where the writer is, and keeps whatever it threw for the one who asked.
internal sealed class Writer(object gate, Allocator allocator)
{
    private readonly Queue<Task> changes = [];

    public Task<T> Run<T>(Func<T> change) => Ask(new Task<T>(change, TaskCreationOptions.RunContinuationsAsynchronously));

    public Task Run(Action change) => Ask(new Task(change, TaskCreationOptions.RunContinuationsAsynchronously));

    private T Ask<T>(T change)
        where T : Task
    {
        lock (changes)
        {
            changes.Enqueue(change);
            if (changes.Count == 1)
            {
                _ = Task.Run(Drain);
            }
        }

        return change;
    }

    private void Drain()
    {
        Task? next;
        lock (changes)
        {
            next = changes.Peek();
        }

        while (next is not null)
        {
            lock (gate)
            {
                next.RunSynchronously();
                allocator.Clean();
            }

            lock (changes)
            {
                changes.Dequeue();
                changes.TryPeek(out next);
            }
        }
    }
}
