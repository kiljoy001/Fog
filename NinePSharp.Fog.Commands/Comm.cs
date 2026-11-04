using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// comm(1), as 9front's comm.c: a line is cut after 2047 bytes, the byte that ends it lost and the
// rest the next line; a last line with no newline is dropped; lines compare as signed chars, as
// far as a NUL.
public sealed class Comm
{
    private const int Lb = 2048;

    private readonly Process process;
    private readonly string[] ldr = [string.Empty, "\t", "\t\t"];
    private bool one;
    private bool two;
    private bool three;

    private Comm(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var comm = new Comm(process);
        var arguments = new Arguments(argv);
        int l = 1;
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case '1':
                    if (!comm.one)
                    {
                        comm.one = true;
                        comm.ldr[1] = string.Empty;
                        comm.ldr[2] = comm.ldr[2][..l--];
                    }

                    break;
                case '2':
                    if (!comm.two)
                    {
                        comm.two = true;
                        comm.ldr[2] = comm.ldr[2][..l--];
                    }

                    break;
                case '3':
                    comm.three = true;
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count < 2)
        {
            await UsageAsync(process);
        }

        await comm.RunAsync(await comm.OpenfilAsync(args[0]), await comm.OpenfilAsync(args[1]));
    }

    private static async Task UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: comm [-123] file1 file2\n"u8.ToArray());
        process.Exits("usage");
    }

    // rd: the next line, ended at its newline or after Lb-1 bytes, with a NUL in its place; false
    // at the end of the file, whatever was read of a line with no newline left behind.
    private static async Task<bool> RdAsync(Biobuf file, byte[] buf)
    {
        int c;
        for (int i = 0; (c = await file.BgetcAsync()) != Biobuf.Beof; i++)
        {
            buf[i] = (byte)c;
            if (c == '\n' || i > Lb - 2)
            {
                buf[i] = 0;
                return true;
            }
        }

        return false;
    }

    // compare: 0 for equal lines, 1 if a is first, 2 if b is.
    private static int Compare(byte[] a, byte[] b)
    {
        int i = 0;
        for (; a[i] == b[i]; i++)
        {
            if (a[i] == 0)
            {
                return 0;
            }
        }

        // Stryker disable once Equality : the loop ends only where the bytes differ
        return (sbyte)a[i] < (sbyte)b[i] ? 1 : 2;
    }

    private async Task RunAsync(Biobuf ib1, Biobuf ib2)
    {
        byte[] lb1 = new byte[Lb], lb2 = new byte[Lb];
        if (!await RdAsync(ib1, lb1))
        {
            if (!await RdAsync(ib2, lb2))
            {
                process.Exits(null);
            }

            await CopyAsync(ib2, lb2, 2);
        }

        if (!await RdAsync(ib2, lb2))
        {
            await CopyAsync(ib1, lb1, 1);
        }

        for (; ;)
        {
            switch (Compare(lb1, lb2))
            {
                case 0:
                    await WrAsync(lb1, 3);
                    if (!await RdAsync(ib1, lb1))
                    {
                        if (!await RdAsync(ib2, lb2))
                        {
                            process.Exits(null);
                        }

                        await CopyAsync(ib2, lb2, 2);
                    }

                    if (!await RdAsync(ib2, lb2))
                    {
                        await CopyAsync(ib1, lb1, 1);
                    }

                    break;
                case 1:
                    await WrAsync(lb1, 1);
                    if (!await RdAsync(ib1, lb1))
                    {
                        await CopyAsync(ib2, lb2, 2);
                    }

                    break;
                default:
                    await WrAsync(lb2, 2);
                    if (!await RdAsync(ib2, lb2))
                    {
                        await CopyAsync(ib1, lb1, 1);
                    }

                    break;
            }
        }
    }

    private async Task WrAsync(byte[] str, int n)
    {
        if (n switch { 1 => one, 2 => two, _ => three })
        {
            return;
        }

        var line = new List<byte>(Encoding.UTF8.GetBytes(ldr[n - 1]));
        line.AddRange(str.TakeWhile(b => b != 0));
        line.Add((byte)'\n');
        await process.WriteAsync(1, line.ToArray());
    }

    private async Task CopyAsync(Biobuf ibuf, byte[] lbuf, int n)
    {
        do
        {
            await WrAsync(lbuf, n);
        }
        while (await RdAsync(ibuf, lbuf));

        process.Exits(null);
    }

    private async Task<Biobuf> OpenfilAsync(string s)
    {
        if (s == "-")
        {
            s = "/fd/0";
        }

        try
        {
            return new Biobuf(process, await process.OpenAsync(s, NinePConstants.OREAD));
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"comm: cannot open {s}: {error.Message}\n"));
            process.Exits("open");
            return null!;
        }
    }
}
