namespace NinePSharp.Fog.Kernel;

internal static class Waiting
{
    // The task's end or the token's cancellation, whichever comes first; the task itself goes on. Both
    // set one signal, as Rendezvous does: a Coyote-rewritten caller controls neither WaitAsync nor a
    // Task.WhenAny of a signal's task. Unregistered rather than disposed, which would wait for a
    // cancellation already running.
    public static async Task UntilAsync(Task task, CancellationToken cancellationToken)
    {
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(() => ended.TrySetResult(false));
        _ = SignalAsync(task, ended);
        bool done = await ended.Task;

        // Stryker disable once Statement : unregistering only spares the token a callback that can no longer change the signal it would set
        registration.Unregister();
        if (!done)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await task;
    }

    private static async Task SignalAsync(Task task, TaskCompletionSource<bool> ended)
    {
        try
        {
            await task;
        }
        catch
        {
            // Whoever waits on the task has its failure.
        }

        ended.TrySetResult(true);
    }
}
