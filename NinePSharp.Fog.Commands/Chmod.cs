using System.Text;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// chmod(1), as 9front's chmod.c.
public static class Chmod
{
    private const uint DMAPPEND = 0x40000000;
    private const uint DMEXCL = 0x20000000;
    private const uint DMTMP = 0x04000000;
    private const uint DMREAD = 4;
    private const uint DMWRITE = 2;
    private const uint DMEXEC = 1;
    private const uint DMRWE = DMREAD | DMWRITE | DMEXEC;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        if (argv.Count < 3)
        {
            await process.WriteAsync(2, "usage: chmod 0777 file ... or chmod [who]op[rwxalt] file ...\n"u8.ToArray());
            process.Exits("usage");
        }

        uint mode = unchecked((uint)Libc.Strtol(argv[1], 8, out int p));
        uint mask = A(DMRWE);
        if (p != argv[1].Length)
        {
            (mask, mode) = Parsemode(argv[1]) ?? await BadModeAsync(process, argv[1]);
        }

        foreach (string file in argv.Skip(2))
        {
            Stat dir;
            try
            {
                dir = await process.StatAsync(file);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"chmod: can't stat {file}: {error.Message}\n"));
                continue;
            }

            Stat d = Process.NullDir;
            var ndir = new Stat(0, d.Type, d.Dev, d.Qid, (dir.Mode & ~mask) | (mode & mask), d.Atime, d.Mtime, d.Length, d.Name, d.Uid, d.Gid, d.Muid);
            try
            {
                await process.WStatAsync(file, ndir);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"chmod: can't wstat {file}: {error.Message}\n"));
            }
        }

        process.Exits(null);
    }

    // [who]op[rwxalt]: the bits the spec sets and the mode it sets them to, or null when it is not one.
    internal static (uint Mask, uint Mode)? Parsemode(string spec)
    {
        uint who = 0;
        int s = 0;
        for (; s < spec.Length && "ugoa".Contains(spec[s], StringComparison.Ordinal); s++)
        {
            who |= spec[s] switch
            {
                'u' => U(DMRWE),
                'g' => G(DMRWE),
                'o' => DMRWE,
                _ => A(DMRWE),
            };
        }

        if (s == spec.Length)
        {
            return null;
        }

        // No one named is everyone.
        uint mask = (s == 0 ? A(DMRWE) : who) | DMAPPEND | DMEXCL | DMTMP;

        char op = spec[s++];
        if (op is not ('+' or '-' or '='))
        {
            return null;
        }

        uint mode = 0;
        for (; s < spec.Length; s++)
        {
            switch (spec[s])
            {
                case 'r':
                    mode |= A(DMREAD);
                    break;
                case 'w':
                    mode |= A(DMWRITE);
                    break;
                case 'x':
                    mode |= A(DMEXEC);
                    break;
                case 'a':
                    mode |= DMAPPEND;
                    break;
                case 'l':
                    mode |= DMEXCL;
                    break;
                case 't':
                    mode |= DMTMP;
                    break;
                default:
                    return null;
            }
        }

        if (op != '=')
        {
            mask &= mode;
        }

        return (mask, op == '-' ? ~mode : mode);
    }

    private static async Task<(uint Mask, uint Mode)> BadModeAsync(Process process, string spec)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"chmod: bad mode: {spec}\n"));
        process.Exits("mode");
        return default;
    }

    private static uint U(uint x) => x << 6;

    private static uint G(uint x) => x << 3;

    private static uint A(uint x) => U(x) | G(x) | x;
}
