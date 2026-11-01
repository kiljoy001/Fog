using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// seq(1), as 9front's seq.c: each value from first by incr to last printed by sprint with the
// format, whose conversion reads the double as its verb says; a flag it does not know, as a
// negative first, ends its options there.
public sealed class Seq
{
    private readonly Process process;
    private double min = 1.0;
    private double max;
    private double incr = 1.0;
    private bool constant;
    private string? format;

    private Seq(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var seq = new Seq(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            if (c == 'w')
            {
                seq.constant = true;
                continue;
            }

            if (c != 'f')
            {
                break;
            }

            string f = arguments.Value() ?? await seq.UsageAsync();
            seq.format = f.EndsWith('\n') ? f : f + "\n";
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count is < 1 or > 3)
        {
            await seq.UsageAsync();
        }

        seq.max = Libc.Strtod(args[^1], out _);
        seq.min = args.Count > 1 ? Libc.Strtod(args[0], out _) : seq.min;
        seq.incr = args.Count > 2 ? Libc.Strtod(args[1], out _) : seq.incr;
        if (seq.incr == 0)
        {
            await process.WriteAsync(2, "seq: zero increment\n"u8.ToArray());
            process.Exits("zero increment");
        }

        seq.format ??= seq.Buildfmt();
        for (double val = seq.min; seq.incr > 0 ? val <= seq.max : val >= seq.max; val += seq.incr)
        {
            byte[] buf = Libc.Sprint(seq.format, [Libc.FmtArg.Of(val)]);
            for (int j = 0; seq.constant && j < buf.Length && buf[j] == ' '; j++)
            {
                buf[j] = (byte)'0';
            }

            await process.WriteAsync(1, buf);
        }

        process.Exits(null);
    }

    private static string G(double val) => Encoding.UTF8.GetString(Libc.Sprint("%g", [Libc.FmtArg.Of(val)]));

    // %g, or with -w a %f as wide and as precise as the widest and most precise of the values as
    // %g prints them, unless one of them takes an exponent.
    private string Buildfmt()
    {
        if (!constant)
        {
            return "%g\n";
        }

        int maxw = 0, maxp = 0;
        for (double val = min; val <= max; val += incr)
        {
            string buf = G(val);
            if (buf.Contains('e', StringComparison.Ordinal))
            {
                return "%g\n";
            }

            int dp = buf.IndexOf('.', StringComparison.Ordinal);
            maxw = Math.Max(maxw, dp < 0 ? buf.Length : dp);
            maxp = Math.Max(maxp, dp < 0 ? 0 : buf.Length - dp - 1);
        }

        maxw += maxp > 0 ? maxp + 1 : 0;
        return $"%{maxw}.{maxp}f\n";
    }

    private async Task<string> UsageAsync()
    {
        await process.WriteAsync(2, "usage: seq [-fformat] [-w] [first [incr]] last\n"u8.ToArray());
        process.Exits("usage");
        return default!;
    }
}
