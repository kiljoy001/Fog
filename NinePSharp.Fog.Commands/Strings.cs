using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// strings(1), as 9front's strings.c: each run of at least minspan printable runes, after the
// offset in bytes of where it would start were each of its first minspan runes one byte.
public sealed class Strings
{
    private readonly Process process;
    private readonly Biobuf fout;
    private int minspan = 6;

    private Strings(Process process)
    {
        this.process = process;
        fout = new Biobuf(process, 1);
    }

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var s = new Strings(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'm':
                    s.minspan = Libc.Strtol(await arguments.EargfAsync(() => UsageAsync(process)), 10, out _);
                    if (s.minspan <= 0)
                    {
                        await UsageAsync(process);
                    }

                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        // malloc of the span cannot be had when its size overflows.
        if (s.minspan == int.MaxValue)
        {
            await process.WriteAsync(2, "strings: out of memory\n"u8.ToArray());
            process.Exits("out of memory");
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count < 1)
        {
            await s.StringitAsync(0);
            await s.fout.BflushAsync();
            process.Exits(null);
        }

        foreach (string name in args)
        {
            if (args.Count > 1)
            {
                await s.fout.BwriteAsync(Encoding.UTF8.GetBytes($"{name}:\n"));
                await s.fout.BflushAsync();
            }

            int fd;
            try
            {
                fd = await process.OpenAsync(name, NinePConstants.OREAD);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"open: {error.Message}\n"));
                continue;
            }

            await s.StringitAsync(fd);
            await process.CloseAsync(fd);
        }

        await s.fout.BflushAsync();
        process.Exits(null);
    }

    private static async Task<string> UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: strings [-m min] [file...]\n"u8.ToArray());
        process.Exits("usage");
        return null!;
    }

    private static bool Isprint(long r) => r != 0xfffd && ((r >= ' ' && r < 0x7f) || r > 0xa0);

    private async Task StringitAsync(int fd)
    {
        var fin = new Biobuf(process, fd);
        List<uint>? sp = [];
        long c;
        while ((c = await fin.BgetruneAsync()) >= 0)
        {
            if (Isprint(c))
            {
                if (sp is null)
                {
                    await fout.BputruneAsync((uint)c);
                    continue;
                }

                sp.Add((uint)c);
                if (sp.Count < minspan)
                {
                    continue;
                }

                await fout.BwriteAsync(Encoding.UTF8.GetBytes($"{fin.Boffset - minspan,8}: "));
                foreach (uint r in sp)
                {
                    await fout.BputruneAsync(r);
                }

                sp = null;
            }
            else
            {
                if (sp is null)
                {
                    await fout.BputruneAsync('\n');
                }

                sp = [];
            }
        }

        if (sp is null)
        {
            await fout.BputruneAsync('\n');
        }
    }
}
