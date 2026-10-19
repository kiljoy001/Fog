using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// sleep(1), as 9front's sleep.c: seconds, and a fraction to the millisecond, without floating point.
public static class Sleep
{
    private const int MaxSec = 0x7ffffc00 / 1000;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        if (argv.Count > 1)
        {
            foreach (long milliseconds in Sleeps(argv[1]))
            {
                await process.SleepAsync(milliseconds);
            }
        }

        process.Exits(null);
    }

    // The sleeps sleep.c makes for its argument, in milliseconds: whole spans of MaxSec seconds
    // while more remain, then the rest with its fraction, of which at most three places count.
    public static IEnumerable<long> Sleeps(string argument)
    {
        int n = Libc.Strtol(argument, 0, out int end);
        for (; n > MaxSec; n -= MaxSec)
        {
            yield return MaxSec * 1000;
        }

        int m = 0;
        string p = argument[end..];
        if (p.StartsWith('.') && (m = Libc.Strtol(p = p[1..], 10, out int q)) > 0)
        {
            m = q switch
            {
                1 => m * 100,
                2 => m * 10,
                _ => Libc.Strtol(p[..3], 10, out _),
            };
        }
        else
        {
            m = 0;
        }

        m = unchecked(m + (n * 1000));
        if (m > 0)
        {
            yield return m;
        }
    }
}
