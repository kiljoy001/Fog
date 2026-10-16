using System.Threading.Channels;

namespace NinePSharp.Fog.Kernel.Tests.Support;

// A forked process that runs a test's calls one at a time, waiting outside any kernel
// call between them, with a handler that records each note and continues or leaves it to its
// default, or with none.
internal sealed class Agent(Process parent, bool? continues, RforkFlags flags = RforkFlags.Proc)
{
    private readonly Channel<Func<Process, Task>> calls = Channel.CreateUnbounded<Func<Process, Task>>();
    private readonly TaskCompletionSource<Process> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> seen = [];
    private TaskCompletionSource<bool> noted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Process Parent { get; } = parent;

    public long Pid { get; private set; }

    public Process Process => started.Task.Result;

    public async Task StartAsync()
    {
        Pid = Parent.Fork(flags, RunAsync);
        await started.Task.WaitAsync(KernelDriver.Bound);
    }

    public async Task<T> Do<T>(Func<Process, Task<T>> call)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await calls.Writer.WriteAsync(async process =>
        {
            try
            {
                result.SetResult(await call(process));
            }
            catch (ExitException)
            {
                result.SetCanceled();
                throw;
            }
            catch (Exception error)
            {
                result.SetException(error);
            }
        });
        return await result.Task;
    }

    public Task Do(Func<Process, Task> call) => Do(async process =>
    {
        await call(process);
        return true;
    });

    public async Task SawAsync(string note)
    {
        using var bound = new CancellationTokenSource(KernelDriver.Bound);
        while (true)
        {
            Task wait;
            lock (seen)
            {
                if (seen.Contains(note))
                {
                    return;
                }

                wait = noted.Task;
            }

            await wait.WaitAsync(bound.Token);
        }
    }

    public void Stop() => calls.Writer.TryComplete();

    private async Task RunAsync(Process process)
    {
        if (continues is { } carryOn)
        {
            process.Notify(note =>
            {
                lock (seen)
                {
                    seen.Add(note);
                    noted.TrySetResult(true);
                    noted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return carryOn;
            });
        }

        started.SetResult(process);
        await foreach (Func<Process, Task> call in calls.Reader.ReadAllAsync())
        {
            await call(process);
        }
    }
}
