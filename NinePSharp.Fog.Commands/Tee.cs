using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// tee(1), as 9front's tee.c.
public static class Tee
{
    private const int IoUnit = 32768;
    private const int Fdstart = 3;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        bool aflag = false;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'a':
                    aflag = true;
                    break;
                case 'i':
                    process.Notify(note => note == "interrupt");
                    break;
                case 'u':
                    // uflag is ignored and undocumented; it's a relic from Unix.
                    break;
                default:
                    await process.WriteAsync(2, "usage: tee [-ai] [file ...]\n"u8.ToArray());
                    process.Exits("usage");
                    break;
            }
        }

        int n = 0;
        foreach (string file in arguments.Rest())
        {
            try
            {
                int i = aflag ? await OpenAppendAsync(process, file) : await process.CreateAsync(file, NinePConstants.OWRITE, 0b110_110_110);
                if (i != n + Fdstart)
                {
                    await process.DupAsync(i, n + Fdstart);
                }

                n++;
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"tee: cannot open {file}: {error.Message}\n"));
            }
        }

        while (true)
        {
            ReadOnlyMemory<byte> r;
            try
            {
                r = await process.ReadAsync(0, IoUnit);
            }
            catch (SyscallException)
            {
                break;
            }

            if (r.IsEmpty)
            {
                break;
            }

            for (int i = 0; i <= n; i++)
            {
                try
                {
                    await process.WriteAsync(i == n ? 1 : i + Fdstart, r);
                }
                catch (SyscallException)
                {
                }
            }
        }

        process.Exits(null);
    }

    private static async Task<int> OpenAppendAsync(Process process, string file)
    {
        int i;
        try
        {
            i = await process.OpenAsync(file, NinePConstants.OWRITE);
        }
        catch (SyscallException)
        {
            i = await process.CreateAsync(file, NinePConstants.OWRITE, 0b110_110_110);
        }

        await process.SeekAsync(i, 0, 2);
        return i;
    }
}
