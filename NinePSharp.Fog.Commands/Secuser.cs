using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// auth/secuser, as 9front's secuser.c: makes or edits a secure-store account, asking on the
// console for its password, and on standard input for its expiry, its status, whether it needs a
// SecurID step, and a comment. Times are GMT, Fog's only zone.
public sealed class Secuser(Process process, string argv0)
{
    private const uint Dmdir = 0x80000000;
    private bool verbose;

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Secuser(process, argv[0]).RunAsync(argv);

    // tm2sec, as tmnorm counts days from the civil date, so days past a month's end run into the
    // next.
    private static long Tm2sec(int mday, int mon, int year, int hour, int min, int sec)
    {
        const long Days400y = 146097, Days4y = 1461, Daysec = 86400;
        (long m, long y) = mon > 1 ? (mon - 2, year + 1900L) : (mon + 10, year + 1899L);
        long c = y / 100;
        long yadj = y - (100 * c);
        long j = (c * Days400y / 4) + (Days4y * yadj / 4) + (((153 * m) + 2) / 5) + mday - 719469;
        return (j * Daysec) + (hour * 3600) + (min * 60) + sec;
    }

    private async Task RunAsync(IReadOnlyList<string> argv)
    {
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            if (c == 'v')
            {
                verbose = true;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count != 1)
        {
            await PrintAsync("usage: secuser [-v] <user>\n");
            process.Exits("usage");
        }

        await EnsureExistsAsync(Password.SecstoreDir, Dmdir | 0b111_101_101);
        await EnsureExistsAsync($"{Password.SecstoreDir}/who", Dmdir | 0b111_101_101);
        await EnsureExistsAsync($"{Password.SecstoreDir}/store", Dmdir | 0b111_000_000);
        string id = args[0];
        if (verbose)
        {
            await PrintAsync($"secuser {id}\n");
        }

        string home = $"{Password.SecstoreDir}/store/{id}";
        (Pw? pw, string? why) = await Password.GetAsync(process, id, true);
        bool isnew = pw is null;
        if (pw is null)
        {
            await OutAsync($"new account (because {Password.SecstoreDir}/{id} {why})\n");
            pw = new Pw(id);
            if (await ExistsAsync(home))
            {
                await SysfatalAsync($"new user, but directory {home} already exists");
            }
        }

        await PasswordAsync(pw, id, isnew);
        uint expsecs = isnew ? (uint)(await Syslog.TimeAsync(process) + (365 * 24 * 60 * 60)) : pw.Expire;
        pw.Expire = await ExpiresAsync(expsecs);

        // Failed logins.
        if (pw.Failed != 0)
        {
            await OutAsync($"clearing {pw.Failed} failed login attempts\n");
        }

        pw.Failed = 0;

        // Status bits.
        if (isnew)
        {
            pw.Status = Pw.Enabled;
        }

        pw.Status = await BitAsync(pw.Status, Pw.Enabled, ('E', 'D'), on => $"Enabled or Disabled [default {(on ? "Enabled" : "Disabled")}]: ");
        pw.Status = await BitAsync(pw.Status, Pw.Sta, ('Y', 'N'), on => $"require STA? [default {(on ? "yes" : "no")}]: ");

        // The free form field.
        if (isnew)
        {
            pw.Other = null;
        }

        await OutAsync($"comments [default = {pw.Other ?? string.Empty}]: ");

        // 72 comes from password.h.
        string comment = await UserinputAsync(72);
        if (comment.Length > 0)
        {
            pw.Other = comment;
        }

        await Syslog.WriteAsync(process, false, Password.Log, $"CHANGELOGIN for '{pw.Id}'");
        if (await Password.PutAsync(process, pw) is { } failed)
        {
            await SysfatalAsync($"can't write password file: {failed}");
        }

        await OutAsync("change written\n");
        if (isnew)
        {
            try
            {
                await process.CloseAsync(await process.CreateAsync(home, NinePConstants.OREAD, Dmdir | 0b111_111_101));
            }
            catch (SyscallException error)
            {
                await SysfatalAsync($"unable to create {home}: {error.Message}");
            }
        }

        process.Exits(string.Empty);
    }

    // The account's password from the console, typed twice, or for an account that has one, kept
    // when none is typed.
    private async Task PasswordAsync(Pw pw, string id, bool isnew)
    {
        string pass = await AskAsync(id, isnew);
        if (pass.Length == 0)
        {
            return;
        }

        if (verbose)
        {
            await OutAsync("confirming...\n");
        }

        string passck = await Authsrv.ReadconsAsync(process, "retype password", null, true) ?? await SysfatalAsync("password input aborted");
        if (passck != pass)
        {
            await SysfatalAsync("passwords didn't match");
        }

        pw.Hi = Pak.Hi(id, pass).Hi;
    }

    // The password typed, asked for again until it is seven bytes, or for an account that has
    // one, empty.
    private async Task<string> AskAsync(string id, bool isnew)
    {
        while (true)
        {
            string prompt = isnew ? $"{id} password" : $"{id} password [default = don't change]";
            string pass = await Authsrv.ReadconsAsync(process, prompt, null, true) ?? await SysfatalAsync("password input aborted");
            int len = Encoding.UTF8.GetByteCount(pass);
            if (verbose)
            {
                await OutAsync($"{len} characters\n");
            }

            if ((len == 0 && !isnew) || len >= 7)
            {
                return pass;
            }

            await OutAsync("password must be at least 7 characters\n");
        }
    }

    // The expiration time: midnight of the date given, as DDMMYYYY, or expsecs for none.
    private async Task<uint> ExpiresAsync(uint expsecs)
    {
        while (true)
        {
            DateTime tm = DateTimeOffset.FromUnixTimeSeconds(expsecs).UtcDateTime;
            await OutAsync($"expires [DDMMYYYY, default = {tm.Day:D2}{tm.Month:D2}{tm.Year:D4}]: ");
            string buf = await UserinputAsync(SConn.Maxmsg);
            if (buf.Length == 0)
            {
                return expsecs;
            }

            if (Encoding.UTF8.GetByteCount(buf) != 8)
            {
                await OutAsync($"!bad date format: {buf}\n");
                continue;
            }

            int mday = ((buf[0] - '0') * 10) + (buf[1] - '0');
            if (mday is > 31 or < 1)
            {
                await OutAsync($"!bad day of month: {mday}\n");
                continue;
            }

            int mon = ((buf[2] - '0') * 10) + (buf[3] - '0') - 1;
            if (mon is > 11 or < 0)
            {
                await OutAsync($"!bad month: {mon + 1}\n");
                continue;
            }

            int year = Libc.Strtol(buf[4..], 10, out _) - 1900;
            if (year < 70)
            {
                await OutAsync($"!bad year: {year + 1900}\n");
                continue;
            }

            return unchecked((uint)Tm2sec(mday, mon, year, 23, 59, 59));
        }
    }

    // A status bit, set or cleared by an answer starting with either letter, as asked until one
    // does or none is given.
    private async Task<ushort> BitAsync(ushort status, ushort bit, (char Set, char Clear) letters, Func<bool, string> prompt)
    {
        while (true)
        {
            await OutAsync(prompt((status & bit) != 0));
            string buf = await UserinputAsync(SConn.Maxmsg);
            if (buf.Length == 0)
            {
                return status;
            }

            char c = char.ToUpperInvariant(buf[0]);
            if (c == letters.Set)
            {
                return (ushort)(status | bit);
            }

            if (c == letters.Clear)
            {
                return (ushort)(status & ~bit);
            }
        }
    }

    // userinput: a line from standard input, read until it ends with a newline.
    private async Task<string> UserinputAsync(int blen)
    {
        var buf = new List<byte>();
        while (true)
        {
            ReadOnlyMemory<byte> b;
            try
            {
                b = await process.ReadAsync(0, blen);
            }
            catch (SyscallException)
            {
                b = ReadOnlyMemory<byte>.Empty;
            }

            if (b.IsEmpty)
            {
                process.Exits("read error");
            }

            buf.AddRange(b.Span);
            if (b.Span[^1] == '\n')
            {
                string line = Encoding.UTF8.GetString([.. buf]);
                int nul = line.IndexOf('\0', StringComparison.Ordinal);
                return nul >= 0 ? line[..nul] : line[..^1];
            }

            blen -= b.Length;
            if (blen <= 0)
            {
                process.Exits("input too large");
            }
        }
    }

    // ensure_exists: f made with perm unless it is there.
    private async Task EnsureExistsAsync(string f, uint perm)
    {
        if (await ExistsAsync(f))
        {
            return;
        }

        if (verbose)
        {
            await PrintAsync($"first time setup for secstore: create {f} {Convert.ToString(perm, 8)}\n");
        }

        try
        {
            await process.CloseAsync(await process.CreateAsync(f, NinePConstants.OREAD, perm));
        }
        catch (SyscallException error)
        {
            await SysfatalAsync($"unable to create {f}: {error.Message}");
        }
    }

    private async Task<bool> ExistsAsync(string f)
    {
        try
        {
            await process.StatAsync(f);
            return true;
        }
        catch (SyscallException)
        {
            return false;
        }
    }

    private async Task<string> SysfatalAsync(string message)
    {
        await PrintAsync($"{argv0}: {message}\n");
        process.Exits(message);
        return string.Empty;
    }

    private Task OutAsync(string text) => process.WriteAsync(1, Encoding.UTF8.GetBytes(text)).AsTask();

    private Task PrintAsync(string text) => process.WriteAsync(2, Encoding.UTF8.GetBytes(text)).AsTask();
}
