namespace NinePSharp.Fog.Thread;

/// <summary>
/// libthread's _threadrendezvous: two threads meeting on a tag exchange values. The first to arrive
/// sleeps until the second comes; an interrupted sleeper leaves the tag and returns <see cref="Interrupted"/>,
/// the ~0 that rendezvous(2) returns. Tags are compared by identity.
/// </summary>
internal static class Rendezvous
{
    internal static readonly object Interrupted = new();

    // rendez.c's _threadrgrp: one table for the whole process. A thread arriving at a tag with a
    // sleeper always meets it, so a tag has at most one sleeper.
    internal static readonly object Lock = new();
    private static readonly Dictionary<object, Sleeper> Sleeping = new(ReferenceEqualityComparer.Instance);

    internal static async Task<object?> MeetAsync(object tag, object? value, CancellationToken interrupt = default)
    {
        Sleeper? sleeper = Arrive(tag, value, out object? met);
        if (sleeper is null)
        {
            return met;
        }

        using (interrupt.Register(() => Break(tag, sleeper)))
        {
            return await sleeper.Wake.Task;
        }
    }

    // Meets the tag's sleeper and returns its value, or starts sleeping on the tag.
    internal static Sleeper? Arrive(object tag, object? value, out object? met)
    {
        lock (Lock)
        {
            if (Sleeping.Remove(tag, out var other))
            {
                other.Wake.TrySetResult(value);
                met = other.Value;
                return null;
            }

            met = null;
            var sleeper = new Sleeper(value);
            Sleeping.Add(tag, sleeper);
            return sleeper;
        }
    }

    // Wakes the sleeper with ~0 if it is still sleeping; an interrupt after it was met changes nothing,
    // even when another sleeper has since taken the tag.
    internal static void Break(object tag, Sleeper sleeper)
    {
        lock (Lock)
        {
            if (Sleeping.TryGetValue(tag, out var current) && current == sleeper)
            {
                Sleeping.Remove(tag);
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
