using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// cmp(1), as 9front's cmp.c, its buffers kept as cmp.c keeps them, so -L counts lines as it does.
public sealed class Cmp
{
    private const int Buf = 65536;

    private readonly Process process;
    private bool sflag;
    private bool lflag;
    private bool bigLflag;

    private Cmp(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var cmp = new Cmp(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 's':
                    cmp.sflag = true;
                    break;
                case 'l':
                    cmp.lflag = true;
                    break;
                case 'L':
                    cmp.bigLflag = true;
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count is < 2 or > 4)
        {
            await UsageAsync(process);
        }

        int f1 = await cmp.OpenAsync(args[0]);
        int f2 = await cmp.OpenAsync(args[1]);
        await cmp.SeekoffAsync(f1, args[0], args.Count > 2 ? args[2] : null);
        await cmp.SeekoffAsync(f2, args[1], args.Count > 3 ? args[3] : null);
        await cmp.CompareAsync(f1, f2, args[0], args[1]);
    }

    private static async Task UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: cmp [-lLs] file1 file2 [offset1 [offset2] ]\n"u8.ToArray());
        process.Exits("usage");
    }

    private async Task PrintAsync(string text) => await process.WriteAsync(1, Encoding.UTF8.GetBytes(text));

    private async Task<int> OpenAsync(string name)
    {
        try
        {
            return await process.OpenAsync(name, NinePConstants.OREAD);
        }
        catch (SyscallException error)
        {
            if (!sflag)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{name}: {error.Message}\n"));
            }

            process.Exits("open");
            return default;
        }
    }

    private async Task SeekoffAsync(int fd, string name, string? arg)
    {
        if (arg is null)
        {
            return;
        }

        if (arg.Length == 0 || !char.IsAsciiDigit(arg[0]))
        {
            await UsageAsync(process);
        }

        long o = Libc.Strtoll(arg, 0, out _);
        try
        {
            await process.SeekAsync(fd, o, 0);
        }
        catch (SyscallException error)
        {
            if (!sflag)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"cmp: {name}: seek by {o}: {error.Message}\n"));
            }

            process.Exits("seek");
        }
    }

    // read into the buffer from s to its end: the end of what it holds, or one before s on an error.
    private async Task<int> FillAsync(int fd, byte[] buf, int s)
    {
        try
        {
            ReadOnlyMemory<byte> read = await process.ReadAsync(fd, Buf - s);
            read.CopyTo(buf.AsMemory(s));
            return s + read.Length;
        }
        catch (SyscallException)
        {
            return s - 1;
        }
    }

    private async Task CompareAsync(int f1, int f2, string name1, string name2)
    {
        byte[] buf1 = new byte[Buf], buf2 = new byte[Buf];
        long nc = 1, l = 1;
        int b1s = 0, b1e = 0, b2s = 0, b2e = 0;
        while (true)
        {
            if (b1s >= b1e)
            {
                // Stryker disable once Conditional : a buffer refilled from its start holds what it would from where it was read to, and is compared from there
                b1s = b1s >= Buf ? 0 : b1s;
                b1e = await FillAsync(f1, buf1, b1s);
            }

            if (b2s >= b2e)
            {
                // Stryker disable once Conditional : a buffer refilled from its start holds what it would from where it was read to, and is compared from there
                b2s = b2s >= Buf ? 0 : b2s;
                b2e = await FillAsync(f2, buf2, b2s);
            }

            int n = Math.Min(b2e - b2s, b1e - b1s);
            if (n <= 0)
            {
                break;
            }

            if (!buf1.AsSpan(b1s, n).SequenceEqual(buf2.AsSpan(b2s, n)))
            {
                if (sflag)
                {
                    process.Exits("differ");
                }

                for (int i = 0; i < n; i++)
                {
                    byte p = buf1[b1s + i], q = buf2[b2s + i];
                    l += p == '\n' ? 1 : 0;
                    if (p != q && !lflag)
                    {
                        await PrintAsync(string.Create(CultureInfo.InvariantCulture, $"{name1} {name2} differ: char {nc + i}{(bigLflag ? $" line {l}" : string.Empty)}\n"));
                        process.Exits("differ");
                    }

                    if (p != q)
                    {
                        await PrintAsync(string.Create(CultureInfo.InvariantCulture, $"{(nc + i).ToString(CultureInfo.InvariantCulture),6} 0x{p:x2} 0x{q:x2}\n"));
                    }
                }
            }

            if (bigLflag)
            {
                l += buf1.AsSpan(b1s, b1e - b1s).Count((byte)'\n');
            }

            nc += n;
            b1s += n;
            b2s += n;
        }

        if (b1e - b1s < 0 || b2e - b2s < 0)
        {
            if (!sflag)
            {
                foreach ((string name, int left) in new[] { (name1, b1e - b1s), (name2, b2e - b2s) })
                {
                    if (left < 0)
                    {
                        await PrintAsync(string.Create(CultureInfo.InvariantCulture, $"error on {name} after {nc - 1} bytes\n"));
                    }
                }
            }

            process.Exits("read error");
        }

        if (b1e - b1s == b2e - b2s)
        {
            process.Exits(null);
        }

        if (!sflag)
        {
            // Stryker disable once Equality : the files' lengths left differ here, as an equal one has exited
            await PrintAsync(string.Create(CultureInfo.InvariantCulture, $"EOF on {(b1e - b1s > b2e - b2s ? name2 : name1)} after {nc - 1} bytes\n"));
        }

        process.Exits("EOF");
    }
}
