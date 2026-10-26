using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// libc's syslog, as 9front's syslog.c: "sysname time message" appended to /sys/log/logname, and
// written to #c/cons as well when asked, or when the log cannot be opened. ctime is GMT, as it is
// without /env/timezone.
public static class Syslog
{
    public static async Task WriteAsync(Process process, bool cons, string logname, string message)
    {
        int fd = await TryOpenAsync(process, $"/sys/log/{logname}");
        int consfd = fd < 0 || cons ? await TryOpenAsync(process, "#c/cons") : -1;
        string line = $"{await SysnameAsync(process)} {Ctime(await TimeAsync(process))} {message}\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        if (fd >= 0)
        {
            await process.SeekAsync(fd, 0, 2);
            await process.WriteAsync(fd, bytes);
            await process.CloseAsync(fd);
        }

        if (consfd >= 0)
        {
            await process.WriteAsync(consfd, bytes);
            await process.CloseAsync(consfd);
        }
    }

    // time: nsec's read of /dev/bintime, in seconds, or 0 when it cannot be opened.
    internal static async Task<long> TimeAsync(Process process)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync("/dev/bintime", NinePConstants.OREAD);
        }
        catch (SyscallException)
        {
            return 0;
        }

        long nanoseconds = BinaryPrimitives.ReadInt64BigEndian((await process.PReadAsync(fd, 8, 0)).Span);
        await process.CloseAsync(fd);
        return nanoseconds / 1_000_000_000;
    }

    // ctime(t)+4, 15 places: the month, the day in two places, and the time of day.
    private static string Ctime(long seconds)
    {
        DateTime t = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return string.Create(CultureInfo.InvariantCulture, $"{t:MMM} {t.Day,2} {t:HH:mm:ss}");
    }

    // sysname: /dev/sysname, read as libc's sysname reads it, or nothing.
    private static async Task<string> SysnameAsync(Process process)
    {
        try
        {
            int fd = await process.OpenAsync("/dev/sysname", NinePConstants.OREAD);
            try
            {
                return Encoding.UTF8.GetString((await process.ReadAsync(fd, 127)).Span);
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            return string.Empty;
        }
    }

    private static async Task<int> TryOpenAsync(Process process, string name)
    {
        try
        {
            return await process.OpenAsync(name, NinePConstants.OWRITE | NinePConstants.OCEXEC);
        }
        catch (SyscallException)
        {
            return -1;
        }
    }
}
