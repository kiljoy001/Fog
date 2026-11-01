using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// uniq(1), as 9front's uniq.c, its lines C strings, ending at a NUL.
public sealed class Uniq
{
    private readonly Process process;
    private readonly List<byte> fout = [];
    private int fields;
    private int letters;
    private int linec;
    private char mode;
    private int uniq;

    private Uniq(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var u = new Uniq(process);
        int f = 0;
        int i = 1;
        for (; i < argv.Count; i++)
        {
            string a = argv[i];
            if (a.StartsWith('-'))
            {
                if (a.Length > 1 && char.IsAsciiDigit(a[1]))
                {
                    u.fields = Atoi(a[1..]);
                }
                else
                {
                    u.mode = a.Length > 1 ? a[1] : '\0';
                }

                continue;
            }

            if (a.StartsWith('+'))
            {
                u.letters = Atoi(a[1..]);
                continue;
            }

            try
            {
                f = await process.OpenAsync(a, NinePConstants.OREAD);
            }
            catch (SyscallException)
            {
                await SysfatalAsync(process, $"cannot open {a}");
            }

            break;
        }

        if (i + 1 < argv.Count)
        {
            await SysfatalAsync(process, $"unexpected argument {argv[i + 1]}");
        }

        await u.RunAsync(new Biobuf(process, f));
    }

    private static int Atoi(string s) => Libc.Strtol(s, 10, out _);

    private static async Task SysfatalAsync(Process process, string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"uniq: {message}\n"));
        process.Exits(message);
    }

    // A line without its newline, as far as a NUL in it.
    private static async Task<byte[]?> GlineAsync(Biobuf fin)
    {
        byte[]? line = await fin.BrdlineAsync((byte)'\n');
        return line is null ? null : line[..(Array.IndexOf(line, (byte)0) is int nul and >= 0 ? nul : line.Length - 1)];
    }

    private async Task RunAsync(Biobuf fin)
    {
        byte[]? b1 = await GlineAsync(fin);
        byte[]? b2;
        while (b1 is not null)
        {
            linec++;
            if ((b2 = await GlineAsync(fin)) is null)
            {
                Pline(b1);
                break;
            }

            if (!Equal(b1, b2))
            {
                Pline(b1);
                linec = 0;
                do
                {
                    linec++;
                    if ((b1 = await GlineAsync(fin)) is null)
                    {
                        Pline(b2);
                        break;
                    }
                }
                while (Equal(b2, b1));

                if (b1 is not null)
                {
                    Pline(b2);
                    linec = 0;
                }
            }
        }

        if (fout.Count != 0)
        {
            await process.WriteAsync(1, fout.ToArray());
        }

        process.Exits(null);
    }

    private void Pline(byte[] buf)
    {
        switch (mode)
        {
            case 'u' when uniq != 0:
                uniq = 0;
                return;
            case 'd' when uniq == 0:
                return;
            case 'c':
                fout.AddRange(Encoding.UTF8.GetBytes(linec.ToString(CultureInfo.InvariantCulture).PadLeft(4) + " "));
                break;
        }

        uniq = 0;
        fout.AddRange(buf);
        fout.Add((byte)'\n');
    }

    private bool Equal(byte[] b1, byte[] b2)
    {
        int i = fields != 0 || letters != 0 ? Skip(b1) : 0;
        int j = fields != 0 || letters != 0 ? Skip(b2) : 0;
        for (; ; i++, j++)
        {
            byte c = i < b1.Length ? b1[i] : (byte)0;
            byte d = j < b2.Length ? b2[j] : (byte)0;
            if (c != d)
            {
                return c == 0 && mode == 's';
            }

            if (c == 0)
            {
                uniq++;
                return true;
            }
        }
    }

    // Past the fields and then the letters uniq skips.
    private int Skip(byte[] s)
    {
        int p = 0;
        for (int nf = 0; nf < fields; nf++)
        {
            while (p < s.Length && s[p] is (byte)' ' or (byte)'\t')
            {
                p++;
            }

            while (p < s.Length && s[p] is not ((byte)' ' or (byte)'\t'))
            {
                p++;
            }
        }

        return Math.Min(p + letters, s.Length);
    }
}
