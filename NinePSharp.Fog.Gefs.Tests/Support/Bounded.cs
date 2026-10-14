namespace NinePSharp.Fog.Gefs.Tests.Support;

// A file server call run on its own task and given a bounded time, so a call stuck behind a lock
// fails its scenario rather than hanging the run.
internal static class Bounded
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static T Run<T>(Func<ValueTask<T>> call) => Task.Run(async () => await call()).WaitAsync(Limit).GetAwaiter().GetResult();

    public static void Run(Func<ValueTask> call) => Task.Run(async () => await call()).WaitAsync(Limit).GetAwaiter().GetResult();
}
