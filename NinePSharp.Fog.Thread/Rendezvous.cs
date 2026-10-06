namespace NinePSharp.Fog.Thread;

/// <summary>
/// rendezvous(2) within one <see cref="RendezvousGroup"/>: two threads meeting on a tag exchange values.
/// The first to arrive sleeps until the second comes; an interrupted sleeper leaves the tag and returns
/// <see cref="Interrupted"/>, the ~0 that rendezvous(2) returns. Tags are compared by identity.
/// </summary>
internal sealed class Rendezvous
{
    internal static readonly object Interrupted = new();

    // A thread arriving at a tag with a sleeper always meets it, so a tag has at most one sleeper.
    private readonly Dictionary<object, Sleeper> sleeping = new(ReferenceEqualityComparer.Instance);

    internal object Lock { get; } = new();

    internal async Task<object?> MeetAsync(object tag, object? value, CancellationToken interrupt = default)
    {
        Sleeper? sleeper = Arrive(tag, value, out object? met);
        if (sleeper is null)
        {
            return met;
        }

        // Unregister rather than dispose: disposing waits for a Break already running, and a late Break
        // changes nothing anyway.
        CancellationTokenRegistration registration = interrupt.Register(() => Break(tag, sleeper));
        try
        {
            return await sleeper.Wake.Task;
        }
        finally
        {
            registration.Unregister();
        }
    }

    // Meets the tag's sleeper and returns its value, or starts sleeping on the tag.
    internal Sleeper? Arrive(object tag, object? value, out object? met)
    {
        lock (Lock)
        {
            if (sleeping.Remove(tag, out var other))
            {
                other.Wake.TrySetResult(value);
                met = other.Value;
                return null;
            }

            met = null;
            var sleeper = new Sleeper(value);
            sleeping.Add(tag, sleeper);
            return sleeper;
        }
    }

    // Wakes the sleeper with ~0 if it is still sleeping; an interrupt after it was met changes nothing,
    // even when another sleeper has since taken the tag.
    internal void Break(object tag, Sleeper sleeper)
    {
        lock (Lock)
        {
            if (sleeping.TryGetValue(tag, out var current) && current == sleeper)
            {
                sleeping.Remove(tag);
                sleeper.Wake.TrySetResult(Interrupted);
            }
        }
    }

    internal sealed class Sleeper(object? value)
    {
        internal object? Value { get; } = value;

        internal TaskCompletionSource<object?> Wake { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
