namespace NinePSharp.Fog.Kernel.Tests.Support;

// A clock whose time is set by the test, and which counts the timers made from it that are not
// yet let go.
public sealed class ManualClock : TimeProvider
{
    private int timers;

    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1000);

    public int Timers => Volatile.Read(ref timers);

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref timers);
        return new Counted(this, base.CreateTimer(callback, state, dueTime, period));
    }

    private sealed class Counted(ManualClock clock, ITimer timer) : ITimer
    {
        private int disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

        public void Dispose()
        {
            Release();
            timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Release();
            return timer.DisposeAsync();
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.Decrement(ref clock.timers);
            }
        }
    }
}
