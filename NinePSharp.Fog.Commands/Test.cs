using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// test(1), as 9front's test.c, whose numbers are Plan 9's 32-bit longs.
public static class Test
{
    private const uint DmAppend = 0x40000000;
    private const uint DmExcl = 0x20000000;
    private const uint DmTmp = 0x04000000;
    private const int AExist = 0;
    private const int AExec = 1;
    private const int AWrite = 2;
    private const int ARead = 4;

    // access's open modes for each access mode.
    private static readonly int[] OpenModes =
    [
        0, NinePConstants.OEXEC, NinePConstants.OWRITE, NinePConstants.ORDWR,
        NinePConstants.OREAD, NinePConstants.OEXEC, NinePConstants.ORDWR, NinePConstants.ORDWR,
    ];

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        List<string> av = [.. argv];
        int ac = av.Count;
        if (av[0] == "[" && av[--ac] != "]")
        {
            await SynbadAsync(process, "] missing", string.Empty);
        }

        if (ac <= 1)
        {
            process.Exits("usage");
        }

        var parser = new Parser(process, av[..ac]);
        bool r = await parser.EAsync(true);
        if (await parser.NxtargAsync(true) is { } c)
        {
            await SynbadAsync(process, "unexpected operator/operand: ", c);
        }

        process.Exits(r ? null : "false");
    }

    private static async Task SynbadAsync(Process process, string s1, string s2)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"test: {s1}{s2}\n"));
        process.Exits("bad syntax");
    }

    // isint: strtol read the whole string.
    private static bool IsInt(string s, out int value)
    {
        value = Libc.Strtol(s, 0, out int end);
        return end == s.Length;
    }

    private sealed class Parser(Process process, List<string> av)
    {
        private readonly int ac = av.Count;
        private int ap = 1;

        public async Task<string?> NxtargAsync(bool mt)
        {
            if (ap >= ac)
            {
                if (mt)
                {
                    return null;
                }

                await SynbadAsync(process, "argument expected", string.Empty);
            }

            return av[ap++];
        }

        public async Task<bool> EAsync(bool eval)
        {
            bool p1 = await E1Async(eval);
            for (string? op; (op = await NxtargAsync(true)) is not null;)
            {
                if (op != "-o")
                {
                    ap--;
                    return p1;
                }

                if (!p1 && eval)
                {
                    p1 = await E1Async(true);
                }
                else
                {
                    await E1Async(false);
                }
            }

            return p1;
        }

        private async Task<bool> E1Async(bool eval)
        {
            bool p1 = await E2Async(eval);
            for (string? op; (op = await NxtargAsync(true)) is not null;)
            {
                if (op != "-a")
                {
                    ap--;
                    return p1;
                }

                if (p1 && eval)
                {
                    p1 = await E2Async(true);
                }
                else
                {
                    await E2Async(false);
                }
            }

            return p1;
        }

        private async Task<bool> E2Async(bool eval)
        {
            bool p1 = false;
            while (true)
            {
                string? op = await NxtargAsync(true);
                if (op is null)
                {
                    return !p1;
                }

                if (op != "!")
                {
                    break;
                }

                p1 = !p1;
            }

            ap--;
            return p1 ^ await E3Async(eval);
        }

        private async Task<bool> E3Async(bool eval)
        {
            // e2 has seen the operand.
            string a = av[ap++];
            if (a == "(")
            {
                bool p1 = await EAsync(eval);
                if (await NxtargAsync(false) != ")")
                {
                    await SynbadAsync(process, ") expected", string.Empty);
                }

                return p1;
            }

            // Each file operator takes its operand whether or not the expression is being evaluated.
            if (FileOperator(a) is { } check)
            {
                string b = (await NxtargAsync(false))!;
                return eval && await check(b);
            }

            switch (a)
            {
                case "-c" or "-b" or "-u" or "-g":
                    return false;
                case "-t":
                    if (ap >= ac)
                    {
                        return eval && await IsattyAsync(1);
                    }

                    if (!IsInt(av[ap], out int fd))
                    {
                        await SynbadAsync(process, "not a valid file descriptor number ", string.Empty);
                    }

                    ap++;
                    return eval && await IsattyAsync(fd);
                case "-n":
                    return await NxtargAsync(false) != string.Empty;
                case "-z":
                    return await NxtargAsync(false) == string.Empty;
            }

            string? p2 = await NxtargAsync(true);
            if (p2 is null)
            {
                return a.Length != 0;
            }

            switch (p2)
            {
                case "=":
                    return await NxtargAsync(false) == a;
                case "!=":
                    return await NxtargAsync(false) != a;
                case "-older":
                    string older = (await NxtargAsync(false))!;
                    return eval && await IsOlderAsync(older, a);
                case "-ot":
                    string than = (await NxtargAsync(false))!;
                    return eval && await MtimesAsync(than, a, (b, c) => b > c);
                case "-nt":
                    string newer = (await NxtargAsync(false))!;
                    return eval && await MtimesAsync(newer, a, (b, c) => b < c);
            }

            if (IsInt(a, out int int1))
            {
                if (ap < ac && IsInt(av[ap], out int int2))
                {
                    ap++;
                    switch (p2)
                    {
                        case "-eq":
                            return int1 == int2;
                        case "-ne":
                            return int1 != int2;
                        case "-gt":
                            return int1 > int2;
                        case "-lt":
                            return int1 < int2;
                        case "-ge":
                            return int1 >= int2;
                        case "-le":
                            return int1 <= int2;
                    }

                    ap--;
                }
            }

            ap--;
            return a.Length != 0;
        }

        private Func<string, Task<bool>>? FileOperator(string a) => a switch
        {
            "-A" => b => HasModeAsync(b, DmAppend),
            "-L" => b => HasModeAsync(b, DmExcl),
            "-T" => b => HasModeAsync(b, DmTmp),
            "-f" => async b => await DirstatAsync(b) is { } dir && (dir.Mode & (uint)NinePConstants.FileMode9P.DMDIR) == 0,
            "-d" => b => HasModeAsync(b, (uint)NinePConstants.FileMode9P.DMDIR),
            "-r" => b => TioAsync(b, ARead),
            "-w" => b => TioAsync(b, AWrite),
            "-x" => b => TioAsync(b, AExec),
            "-e" => b => TioAsync(b, AExist),
            "-s" => async b => await DirstatAsync(b) is { Length: > 0 },
            _ => null,
        };

        // dirstat: nil for a file that cannot be stated.
        private async Task<Stat?> DirstatAsync(string f)
        {
            Stat? dir = null;
            try
            {
                dir = await process.StatAsync(f);
            }
            catch (SyscallException)
            {
            }

            return dir;
        }

        private async Task<bool> HasModeAsync(string f, uint m) => await DirstatAsync(f) is { } dir && (dir.Mode & m) != 0;

        // access: whether the file is there, or opens for what is asked.
        private async Task<bool> TioAsync(string a, int f)
        {
            if (f == AExist)
            {
                return await DirstatAsync(a) is not null;
            }

            try
            {
                await process.CloseAsync(await process.OpenAsync(a, OpenModes[f & 7]));
                return true;
            }
            catch (SyscallException)
            {
                return false;
            }
        }

        // isatty: the descriptor is /dev/cons.
        private async Task<bool> IsattyAsync(int fd)
        {
            Stat d1;
            try
            {
                d1 = await process.FStatAsync(fd);
            }
            catch (SyscallException)
            {
                return false;
            }

            return await DirstatAsync("/dev/cons") is { } d2 && d1.Type == d2.Type && d1.Dev == d2.Dev && d1.Qid.Path == d2.Qid.Path;
        }

        // isolder: a time with units is that long before now; one without is a time in seconds.
        private async Task<bool> IsOlderAsync(string pin, string f)
        {
            if (await DirstatAsync(f) is not { } dir)
            {
                return false;
            }

            uint n = 0;
            bool relative = true;
            for (string p = pin; p.Length != 0;)
            {
                uint m = Libc.Strtoul(p, 0, out int end);
                p = p[end..];
                if (p.Length == 0)
                {
                    n = m;
                    relative = false;
                    break;
                }

                switch (p[0])
                {
                    case 'y':
                        m *= 12 * 30 * 24 * 60 * 60;
                        break;
                    case 'M':
                        m *= 30 * 24 * 60 * 60;
                        break;
                    case 'd':
                        m *= 24 * 60 * 60;
                        break;
                    case 'h':
                        m *= 60 * 60;
                        break;
                    case 'm':
                        m *= 60;
                        break;
                    case 's':
                        break;
                    default:
                        await SynbadAsync(process, "bad time syntax, ", pin);
                        break;
                }

                n += m;
                p = p[1..];
            }

            if (relative)
            {
                n = (uint)await TimeAsync() - n;
            }

            return dir.Mtime < n;
        }

        private async Task<bool> MtimesAsync(string a, string b, Func<uint, uint, bool> compare)
            => await DirstatAsync(a) is { } ad && await DirstatAsync(b) is { } bd && compare(ad.Mtime, bd.Mtime);

        // time: nsec's read of /dev/bintime, in seconds, from the descriptor libc keeps open for it.
        private async Task<long> TimeAsync()
        {
            int fd = await process.OpenAsync("/dev/bintime", NinePConstants.OREAD);
            return BinaryPrimitives.ReadInt64BigEndian((await process.PReadAsync(fd, 8, 0)).Span) / 1_000_000_000;
        }
    }
}
