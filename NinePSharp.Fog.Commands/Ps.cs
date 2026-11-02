using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// ps(1), as 9front's ps.c: a line for each process in /proc, in the order of their numbers, from
// its status file: the user, the pid, the user and system time, the memory, the state and the
// text, or with -a the arguments; -n adds the note group, -r the real time, -p the priorities.
public sealed class Ps
{
    private readonly Process process;
    private readonly Biobuf bout;
    private bool aflag;
    private bool pflag;
    private bool nflag;
    private bool rflag;

    private Ps(Process process) => (this.process, bout) = (process, new Biobuf(process, 1));

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var ps = new Ps(process);
        foreach (char c in new Arguments(argv).Flags())
        {
            switch (c)
            {
                case 'a':
                    ps.aflag = true;
                    break;
                case 'p':
                    ps.pflag = true;
                    break;
                case 'n':
                    ps.nflag = true;
                    break;
                case 'r':
                    ps.rflag = true;
                    break;
            }
        }

        IReadOnlyList<Stat> dir = [];
        try
        {
            await process.ChdirAsync("/proc");
            int fd = await process.OpenAsync(".", NinePConstants.OREAD);
            try
            {
                dir = await process.DirReadAsync(fd);
            }
            catch (SyscallException)
            {
                // dirreadall's error is a directory of nothing.
            }
        }
        catch (SyscallException e)
        {
            await ps.ErrorAsync("/proc", e.Message);
        }

        if (dir.Count == 0)
        {
            await process.WriteAsync(2, "ps: empty directory /proc\n"u8.ToArray());
            process.Exits("empty");
        }

        foreach (Stat d in dir.OrderBy(d => Atoi(d.Name)))
        {
            await ps.PsAsync(d.Name);
        }

        await ps.bout.BflushAsync();

        // Stryker disable once Statement : a program of Fog's that returns exits with no status, as this does
        process.Exits(null);
    }

    private static int Atoi(string s) => Libc.Strtol(s, 10, out _);

    private static byte[] Sprint(string format, params object[] args)
        => Libc.Sprint(format, [.. args.Select(a => a is string s ? Libc.FmtArg.Of(s) : Libc.FmtArg.Of(Convert.ToInt64(a)))]);

    // The bytes as a C string holds them: up to the first NUL.
    private static byte[] Cut(byte[] s) => Array.IndexOf(s, (byte)0) is int nul and >= 0 ? s[..nul] : s;

    private async Task PsAsync(string s)
    {
        if (await ReadAsync($"{s}/status", 4096 - 1) is not { Length: > 0 } status)
        {
            return;
        }

        // 0 text, 1 user, 2 state, 3 the times, 9 the memory, 10 the base priority, 11 the priority.
        List<string> argv = Process.Tokenize(Encoding.UTF8.GetString(Cut(status)), 16 - 1);
        if (argv.Count < 12)
        {
            return;
        }

        uint utime = Libc.Strtoul(argv[3], 0, out _) / 1000;
        uint stime = Libc.Strtoul(argv[4], 0, out _) / 1000;
        uint rtime = Libc.Strtoul(argv[5], 0, out _) / 1000;
        uint size = Libc.Strtoul(argv[9], 0, out _);
        string nbuf = string.Empty;
        if (nflag)
        {
            nbuf = $" {"?",8}";
            if (await ReadAsync($"{s}/noteid", 64 - 1) is { Length: > 0 } noteid)
            {
                nbuf = Encoding.UTF8.GetString(Sprint(" %7ud", Atoi(Encoding.UTF8.GetString(Cut(noteid)))));
            }

            // snprint into 13 bytes.
            nbuf = nbuf[..Math.Min(nbuf.Length, 12)];
        }

        string pbuf = pflag ? Encoding.UTF8.GetString(Sprint(" %2d %2d", (int)Libc.Strtoul(argv[10], 0, out _), (int)Libc.Strtoul(argv[11], 0, out _))) : string.Empty;
        string rbuf1 = string.Empty;
        if (rflag)
        {
            byte[] rbuf = rtime >= 86400 ? Sprint(" %lud:%02lud:%02lud:%02lud", rtime / 86400, (rtime / 3600) % 24, (rtime / 60) % 60, rtime % 60)
                : rtime >= 3600 ? Sprint(" %lud:%02lud:%02lud", rtime / 3600, (rtime / 60) % 60, rtime % 60)
                : Sprint(" %lud:%02lud", rtime / 60, rtime % 60);
            rbuf1 = Encoding.UTF8.GetString(Sprint("%12s", Encoding.UTF8.GetString(rbuf)));
        }

        await bout.BwriteAsync(Sprint("%-10s %8s%s%s %4lud:%.2lud %3lud:%.2lud %s %7ludK %-8.8s ", argv[1], s, nbuf, rbuf1, utime / 60, utime % 60, stime / 60, stime % 60, pbuf, size, argv[2]));
        if (!aflag)
        {
            await bout.BwriteAsync(Sprint("%s\n", argv[0]));
            return;
        }

        byte[]? args = await ReadAsync($"{s}/args", 256 - 1);
        if (args is null)
        {
            await bout.BwriteAsync(Sprint("%s ?\n", argv[0]));
            return;
        }

        if (args.Length == 0)
        {
            await bout.BwriteAsync(Sprint("%s\n", argv[0]));
            return;
        }

        await bout.BwriteAsync(Cut(args).Select(b => b == '\n' ? (byte)' ' : b).Append((byte)'\n').ToArray());
    }

    // What one read of the file gives, or null when it cannot be opened or read.
    private async Task<byte[]?> ReadAsync(string path, int count)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync(path, NinePConstants.OREAD);
        }
        catch (SyscallException)
        {
            return null;
        }

        try
        {
            return (await process.ReadAsync(fd, count)).ToArray();
        }
        catch (SyscallException)
        {
            return null;
        }
        finally
        {
            // Stryker disable once Statement : the descriptor is closed as ps exits, if not before
            await process.CloseAsync(fd);
        }
    }

    // error: the message, and perror's "error" with errstr.
    private async Task ErrorAsync(string s, string errstr)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"ps: {s}: error: {errstr}\n"));
        process.Exits(s);
    }
}
