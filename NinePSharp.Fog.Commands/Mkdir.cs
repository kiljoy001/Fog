using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// mkdir(1), as 9front's mkdir.c.
public static class Mkdir
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        uint mode = 0b111_111_111;
        bool pflag = false;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'm':
                    string? m = arguments.Value();
                    if (m is null || (mode = Octal(m)) > 0b111_111_111)
                    {
                        await UsageAsync(process);
                    }

                    break;
                case 'p':
                    pflag = true;
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        bool failed = false;
        foreach (string dir in arguments.Rest())
        {
            if (!(pflag ? await MkdirpAsync(process, dir, mode) : await MakedirAsync(process, dir, mode)))
            {
                failed = true;
            }
        }

        process.Exits(failed ? "error" : null);
    }

    // strtoul(m, &m, 8) into a 32-bit ulong: spaces, a sign, then the octal digits, as many as fit.
    private static uint Octal(string s)
    {
        string t = s.TrimStart();
        bool negative = t.StartsWith('-');
        t = negative || t.StartsWith('+') ? t[1..] : t;
        ulong n = 0;
        foreach (char c in t.TakeWhile(c => c is >= '0' and <= '7'))
        {
            n = Math.Min((n * 8) + (uint)(c - '0'), uint.MaxValue);
        }

        return n == uint.MaxValue || !negative ? (uint)n : (uint)-(long)n;
    }

    private static async Task UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: mkdir [-p] [-m mode] dir...\n"u8.ToArray());
        process.Exits("usage");
    }

    private static async Task<bool> ExistsAsync(Process process, string s)
    {
        try
        {
            await process.StatAsync(s);
            return true;
        }
        catch (SyscallException)
        {
            return false;
        }
    }

    // makedir: whether it made the directory.
    private static async Task<bool> MakedirAsync(Process process, string s, uint mode)
    {
        if (await ExistsAsync(process, s))
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"mkdir: {s} already exists\n"));
            return false;
        }

        try
        {
            await process.CloseAsync(await process.CreateAsync(s, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | mode));
            return true;
        }
        catch (SyscallException e)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"mkdir: can't create {s}: {e.Message}\n"));
            return false;
        }
    }

    // mkdirp: each directory along the path that is missing.
    private static async Task<bool> MkdirpAsync(Process process, string s, uint mode)
    {
        for (int p = s.IndexOf('/', Math.Min(1, s.Length)); p != -1; p = s.IndexOf('/', p + 1))
        {
            if (!await ExistsAsync(process, s[..p]) && !await MakedirAsync(process, s[..p], mode))
            {
                return false;
            }
        }

        return await ExistsAsync(process, s) || await MakedirAsync(process, s, mode);
    }
}
