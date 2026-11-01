using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// date(1), as 9front's date.c: the time now, or the seconds given, in the zone of $timezone or,
// with -u, GMT, laid out by tmfmt's format; with -n the seconds themselves.
public static class Date
{
    private const long Nsec = 1000 * 1000 * 1000;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        bool nflg = false, uflg = false;
        string fmt = Tm.Ctimefmt;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'n':
                    nflg = true;
                    break;
                case 'u':
                    uflg = true;
                    break;
                case 't':
                    fmt = "YYYY-MM-DDThh:mm:ssZZ";
                    break;
                case 'i':
                    fmt = "YYYY-MM-DD";
                    break;
                case 'f':
                    fmt = await arguments.EargfAsync(() => UsageAsync(process));
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        long s = 0, ns = 0;
        switch (args.Count)
        {
            case 0:
                ns = await NsecAsync(process);
                (s, ns) = (ns / Nsec, ns % Nsec);
                break;
            case 1:
                s = Libc.Strtoll(args[0], 0, out _);
                break;
            default:
                await UsageAsync(process);
                break;
        }

        Tm.Tzone? tz = null;
        if (!uflg && (tz = await Tm.TzloadAsync(process, "local")) is null)
        {
            await SysfatalAsync(process, $"timezone: {Errstr.Get(process)}");
        }

        Tm tm = Tm.Tmtimens(s, ns, tz);
        if (nflg)
        {
            await process.WriteAsync(1, Encoding.UTF8.GetBytes($"{tm.Tmnorm()}\n"));
        }
        else if (tm.Format(fmt, out string? error) is { } text)
        {
            await process.WriteAsync(1, text.Append((byte)'\n').ToArray());
        }
        else
        {
            await SysfatalAsync(process, error!);
        }

        process.Exits(null);
    }

    // nsec: /dev/bintime's nanoseconds, or 0 when it cannot be read.
    private static async Task<long> NsecAsync(Process process)
    {
        try
        {
            int fd = await process.OpenAsync("/dev/bintime", NinePConstants.OREAD | NinePConstants.OCEXEC);
            ReadOnlyMemory<byte> read = await process.PReadAsync(fd, 8, 0);
            await process.CloseAsync(fd);
            return read.Length == 8 ? BinaryPrimitives.ReadInt64BigEndian(read.Span) : 0;
        }
        catch (SyscallException)
        {
            return 0;
        }
    }

    private static async Task SysfatalAsync(Process process, string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"date: {message}\n"));
        process.Exits(message);
    }

    private static async Task<string> UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: date [-itun] [-f fmt] [seconds]\n"u8.ToArray());
        process.Exits("usage");
        return default!;
    }
}
