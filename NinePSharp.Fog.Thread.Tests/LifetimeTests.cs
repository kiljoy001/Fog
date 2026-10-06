using System.Runtime.CompilerServices;
using Xunit;

namespace NinePSharp.Fog.Thread.Tests;

public sealed class LifetimeTests
{
    // An interrupt token outlives many rendezvous; once one is over, the token must not keep it alive.
    [Fact]
    public async Task AFinishedRendezvousIsNotKeptAliveByItsInterrupt()
    {
        using var interrupt = new CancellationTokenSource();
        WeakReference tag = await MeetAsync(interrupt.Token);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(tag.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> MeetAsync(CancellationToken interrupt)
    {
        var rendezvous = new Rendezvous();
        var tag = new object();
        Task<object?> first = rendezvous.MeetAsync(tag, "first", interrupt);
        Assert.Equal("first", await rendezvous.MeetAsync(tag, "second").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("second", await first.WaitAsync(TimeSpan.FromSeconds(10)));
        return new WeakReference(tag);
    }
}
