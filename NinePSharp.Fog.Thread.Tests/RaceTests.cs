using Xunit;

namespace NinePSharp.Fog.Thread.Tests;

// Windows too narrow to hit by chance, held open by holding the rendezvous lock.
public sealed class RaceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ACloseLeavesAReceiverAlreadyCommittedToItsSender()
    {
        var channel = new Channel<int>();
        Task<(int Result, int Value)> receive = channel.RecvAsync();
        Until(() => channel.Entries.Count == 1);
        Task<int> send;
        Task<int> close;
        Monitor.Enter(Rendezvous.Lock);
        try
        {
            // The sender commits the receiver, then waits for the lock to meet it.
            send = Task.Run(() => channel.SendAsync(4));
            Until(() => channel.Entries[0].Tag!.Taken);
            close = channel.CloseAsync();
        }
        finally
        {
            Monitor.Exit(Rendezvous.Lock);
        }

        Assert.Equal(0, await close.WaitAsync(Wait));
        Assert.Equal((1, 4), await receive.WaitAsync(Wait));
        Assert.Equal(1, await send.WaitAsync(Wait));
    }

    [Fact]
    public async Task AnInterruptAfterTheMeetingLeavesTheTagsNextSleeper()
    {
        var tag = new object();
        Rendezvous.Sleeper first = Rendezvous.Arrive(tag, "first", out _)!;
        Assert.Equal("first", await Rendezvous.MeetAsync(tag, "second").WaitAsync(Wait));
        Rendezvous.Sleeper third = Rendezvous.Arrive(tag, "third", out _)!;

        Rendezvous.Break(tag, first);

        Assert.Equal("second", await first.Wake.Task.WaitAsync(Wait));
        Assert.False(third.Wake.Task.IsCompleted);
        Task<object?> fourth = Rendezvous.MeetAsync(tag, "fourth");
        Assert.True(fourth.IsCompleted, "the tag's sleeper was gone");
        Assert.Equal("third", fourth.Result);
        Assert.Equal("fourth", await third.Wake.Task.WaitAsync(Wait));
    }

    [Fact]
    public void AnInterruptWakesItsSleeperAtOnce()
    {
        var tag = new object();
        Rendezvous.Sleeper sleeper = Rendezvous.Arrive(tag, "first", out _)!;

        Rendezvous.Break(tag, sleeper);

        Assert.True(sleeper.Wake.Task.IsCompleted);
        Assert.Same(Rendezvous.Interrupted, sleeper.Wake.Task.Result);
        Assert.NotNull(Rendezvous.Arrive(tag, "second", out _));
    }

    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            lock (Channel.Lock)
            {
                if (condition())
                {
                    return;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "the expected state never came");
            System.Threading.Thread.Sleep(1);
        }
    }
}
