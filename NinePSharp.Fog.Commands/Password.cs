using System.Numerics;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// secstore's password.c, for the accounts in /adm/secstore/who, and util.c's validatefile.
public static class Password
{
    public const string Log = "secstore";
    public const string SecstoreDir = "/adm/secstore";

    // validatefile: f, unless it is empty, .., too long, or holds a control character, a slash, or
    // a byte past ASCII, which a signed char gives as negative.
    public static async Task<string?> ValidatefileAsync(Process process, string? f)
    {
        if (string.IsNullOrEmpty(f))
        {
            return null;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(f);
        if (f == ".." || bytes.Length >= 250 || bytes.Any(b => b < 040 || b >= 0x80 || b == '/'))
        {
            await Syslog.WriteAsync(process, false, Log, $"illegal name: {f}");
            return null;
        }

        return f;
    }

    // getPW: id's account, or FICTITIOUS's for an id with none; nil and why when neither is, or
    // when the account is expired, disabled, or has failed ten times in the last five minutes,
    // unless it is wanted dead or alive. Five minutes on, the failures are forgotten.
    public static async Task<(Pw? Pw, string? Why)> GetAsync(Process process, string id, bool deadOrAlive)
    {
        long now = await Syslog.TimeAsync(process);
        string oid = id;
        string? text = await OpenAsync(process, id);
        if (text is null)
        {
            id = "FICTITIOUS";
            text = await OpenAsync(process, id);
            if (text is null)
            {
                return (null, $"accounts {oid} and FICTITIOUS do not exist");
            }
        }

        var pw = new Pw(id) { Status = Pw.Enabled };
        BigInteger? hi = null;
        string[] lines = text.Split('\n');

        // Brdline gives only lines a newline ends.
        foreach (string line in lines.Take(lines.Length - 1))
        {
            int f2 = line.IndexOfAny([' ', '\t']);
            string f1 = f2 < 0 ? line : line[..f2];
            string value = f2 < 0 ? string.Empty : line[f2..].TrimStart(' ', '\t');
            switch (f1)
            {
                case "exp":
                    pw.Expire = Libc.Strtoul(value, 10, out _);
                    break;
                case "DISABLED":
                    pw.Status &= unchecked((ushort)~Pw.Enabled);
                    break;
                case "STA":
                    pw.Status |= Pw.Sta;
                    break;
                case "failed":
                    pw.Failed = unchecked((ushort)Libc.Strtoul(value, 10, out _));
                    break;
                case "other":
                    pw.Other = value;
                    break;
                case "PAK-Hi":
                    hi = Pak.Strtomp64(value);
                    break;
            }
        }

        if (hi is not { } h)
        {
            return (null, $"corrupted account file for {pw.Id}");
        }

        pw.Hi = h;
        if (deadOrAlive)
        {
            // For editing, whether valid now or not.
            return (pw, null);
        }

        if (pw.Expire != 0 && pw.Expire <= now)
        {
            // %.28s leaves out ctime's newline.
            return (null, $"account {pw.Id} expired at {Libc.Ctime(pw.Expire)[..28]}");
        }

        if ((pw.Status & Pw.Enabled) == 0)
        {
            return (null, $"account {pw.Id} disabled");
        }

        if (pw.Failed < 10)
        {
            return (pw, null);
        }

        if (now < await MtimeAsync(process, id) + 300)
        {
            return (null, "too many failures; try again in five minutes");
        }

        // The failed-login counter reset after five minutes.
        pw.Failed = 0;
        await PutAsync(process, pw);
        return (pw, null);
    }

    // putPW: the account written, as Bopen makes its file for writing; or why it could not be.
    public static async Task<string?> PutAsync(Process process, Pw pw)
    {
        var text = new StringBuilder();
        text.Append($"exp\t{pw.Expire}\n");
        if ((pw.Status & Pw.Enabled) == 0)
        {
            text.Append("DISABLED\n");
        }

        if ((pw.Status & Pw.Sta) != 0)
        {
            text.Append("STA\n");
        }

        if (pw.Failed != 0)
        {
            text.Append($"failed\t{pw.Failed}\n");
        }

        if (pw.Other is not null)
        {
            text.Append($"other\t{pw.Other}\n");
        }

        text.Append($"PAK-Hi\t{Pak.Mptoa64(pw.Hi)}\n");
        if (await ValidatefileAsync(process, pw.Id) is null || pw.Id == ".")
        {
            return $"can't open PW file for {pw.Id}";
        }

        int fd;
        try
        {
            fd = await process.CreateAsync($"{SecstoreDir}/who/{pw.Id}", NinePConstants.OWRITE, 0b110_110_110);
        }
        catch (SyscallException)
        {
            return $"can't open PW file for {pw.Id}";
        }

        try
        {
            await process.WriteAsync(fd, Encoding.UTF8.GetBytes(text.ToString()));
        }
        finally
        {
            await process.CloseAsync(fd);
        }

        return null;
    }

    // openPW for reading: the account file's text, or null.
    private static async Task<string?> OpenAsync(Process process, string id)
    {
        if (await ValidatefileAsync(process, id) is null || id == ".")
        {
            return null;
        }

        int fd;
        try
        {
            fd = await process.OpenAsync($"{SecstoreDir}/who/{id}", NinePConstants.OREAD);
        }
        catch (SyscallException)
        {
            return null;
        }

        try
        {
            var all = new List<byte>();
            for (ReadOnlyMemory<byte> b; !(b = await process.ReadAsync(fd, 8192)).IsEmpty;)
            {
                all.AddRange(b.Span);
            }

            return Encoding.UTF8.GetString([.. all]);
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    // mtimePW: when the account file was last changed, or 0.
    private static async Task<long> MtimeAsync(Process process, string id)
    {
        try
        {
            return (await process.StatAsync($"{SecstoreDir}/who/{id}")).Mtime;
        }
        catch (SyscallException)
        {
            return 0;
        }
    }
}
