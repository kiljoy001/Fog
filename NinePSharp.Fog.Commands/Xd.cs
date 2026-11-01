using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// xd(1), as 9front's xd.c: each format its own line of each 16 bytes, its address printed as the
// -a given before it says, its output through a Biobuf whose errors are fatal, as Blethal makes
// them, and sysfatal printing them alone, as xd never sets argv0.
public sealed class Xd
{
    private const int Narg = 10;

    // The digits %.Nu gives each length in each base: octal, decimal and hex.
    private static readonly int[][] Digits = [[3, 3, 2], [6, 5, 4], [11, 10, 8], [22, 20, 16]];
    private static readonly int[] Bases = [8, 10, 16];

    private readonly Process process;
    private readonly Biobuf bout;
    private readonly Arg[] arg = new Arg[Narg];
    private readonly byte[] odata = new byte[16];
    private readonly byte[] data = new byte[16];
    private int ndata;
    private uint addr;
    private bool repeats;
    private bool le;
    private bool flush;
    private int abase = 2;
    private int narg;

    private Xd(Process process)
    {
        this.process = process;
        bout = new Biobuf(process, 1) { Errorf = SysfatalAsync };
    }

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var xd = new Xd(process);
        int a = 1;
        while (a < argv.Count && argv[a].Length > 1 && argv[a][0] == '-')
        {
            string s = argv[a++];
            switch (s[1])
            {
                case 'r':
                    xd.repeats = true;
                    await xd.OnlyAsync(s, 1);
                    continue;
                case 's':
                    xd.le = true;
                    await xd.OnlyAsync(s, 1);
                    continue;
                case 'u':
                    xd.flush = true;
                    await xd.OnlyAsync(s, 1);
                    continue;
                case 'a':
                    xd.abase = At(s, 2) switch
                    {
                        'o' => 0,
                        'd' => 1,
                        'x' => 2,
                        _ => await xd.UsageAsync(),
                    };
                    await xd.OnlyAsync(s, 2);
                    continue;
            }

            int ap = xd.narg;
            await xd.InitargAsync();
            Arg f = xd.arg[ap];
            for (int k = 1; k < s.Length; k++)
            {
                switch (s[k])
                {
                    case 'c':
                        (f.Ascii, f.Loglen) = (true, 0);
                        if (k + 1 < s.Length || s[k - 1] != '-')
                        {
                            await xd.UsageAsync();
                        }

                        break;
                    case 'o' or 'd' or 'x':
                        f.Base = "odx".IndexOf(s[k], StringComparison.Ordinal);
                        break;
                    case 'b' or '1':
                        f.Loglen = 0;
                        break;
                    case 'w' or '2':
                        f.Loglen = 1;
                        break;
                    case 'l' or '4':
                        f.Loglen = 2;
                        break;
                    case 'v' or '8':
                        f.Loglen = 3;
                        break;
                    default:
                        await xd.UsageAsync();
                        break;
                }
            }

            f.Afmt = (ap > 0, xd.abase);
        }

        if (xd.narg == 0)
        {
            await xd.InitargAsync();
        }

        bool err = false;
        int files = argv.Count - a;
        if (files <= 1)
        {
            err = await xd.XdAsync(files == 0 ? null : argv[a], false);
        }
        else
        {
            for (; a < argv.Count; a++)
            {
                err |= await xd.XdAsync(argv[a], true);
            }
        }

        await xd.ExitsAsync(err ? "error" : null);
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    // %.Nu, %Nu or %u in a base: at least min digits, padded with zeros.
    private static string Number(ulong v, int b, int min)
    {
        var digits = new StringBuilder();
        do
        {
            digits.Insert(0, "0123456789abcdef"[(int)(v % (ulong)Bases[b])]);
            v /= (ulong)Bases[b];
        }
        while (v != 0);

        return digits.ToString().PadLeft(min, '0');
    }

    // The bytes from data[i] on, big endian or, with -s, little endian, as fmt1 and fmt2 make an
    // int of them.
    private int Word(int i, int n)
    {
        int v = 0;
        for (int k = 0; k < n; k++)
        {
            v = (v << 8) | data[le ? i + n - 1 - k : i + k];
        }

        return v;
    }

    // A flag that must be alone in its argument.
    private async Task OnlyAsync(string s, int k)
    {
        if (At(s, k + 1) != '\0')
        {
            await UsageAsync();
        }
    }

    private async Task<int> UsageAsync()
    {
        await process.WriteAsync(2, "usage: xd [-u] [-r] [-s] [-a{odx}] [-c|{b1w2l4v8}{odx}] ... file ...\n"u8.ToArray());
        await ExitsAsync("usage");
        return 0;
    }

    private async Task InitargAsync()
    {
        int ap = narg++;
        if (narg >= Narg)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"xd: too many formats (max {Narg})\n"));
            await ExitsAsync("usage");
        }

        arg[ap] = new Arg { Afmt = (narg > 1, abase) };
    }

    // Whether name could not be opened.
    private async Task<bool> XdAsync(string? name, bool title)
    {
        int fd = 0;
        if (name is not null)
        {
            try
            {
                fd = await process.OpenAsync(name, NinePConstants.OREAD);
            }
            catch (SyscallException)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"xd: can't open {name}\n"));
                return true;
            }
        }

        var bp = new Biobuf(process, fd) { Errorf = SysfatalAsync, Iof = flush ? async () => await bout.BflushAsync() : null };
        if (title)
        {
            await XprintAsync($"{name}\n");
        }

        addr = 0;
        bool star = false;
        while ((ndata = await bp.BreadAsync(data, 16)) >= 0)
        {
            Array.Clear(data, ndata, 16 - ndata);
            if (ndata == 16 && repeats)
            {
                if (addr > 0 && data.AsSpan().SequenceEqual(odata))
                {
                    addr += 16;
                    if (!star)
                    {
                        star = true;
                        await XprintAsync("*\n");
                    }

                    continue;
                }

                data.CopyTo(odata, 0);
                star = false;
            }

            for (int ap = 0; ap < narg; ap++)
            {
                Arg f = arg[ap];
                (bool second, int b) = f.Afmt;
                await XprintAsync((second ? Number(addr, b, 1).PadLeft(7) : Number(addr, b, 7)) + " ");
                await XprintAsync(f.Ascii ? Fmtc() : Format(f));
                await XprintAsync("\n");
            }

            addr += (uint)ndata;
            if (ndata < 16)
            {
                await XprintAsync(Number(addr, abase, 7) + " \n");
                break;
            }
        }

        if (name is not null)
        {
            await process.CloseAsync(fd);
        }

        return false;
    }

    // fmt0 to fmt3: each byte, short, long or vlong the data has begun, in the format's base.
    private string Format(Arg f)
    {
        var line = new StringBuilder();
        int n = 1 << f.Loglen;
        for (int i = 0; i < ndata; i += n)
        {
            ulong v = n switch
            {
                1 => data[i],
                2 => (ulong)Word(i, 2),
                4 => (uint)Word(i, 4),
                _ => ((ulong)(uint)Word(le ? i + 4 : i, 4) << 32) | (uint)Word(le ? i : i + 4, 4),
            };
            line.Append(' ').Append(Number(v, f.Base, Digits[f.Loglen][f.Base]));
        }

        return line.ToString();
    }

    private string Fmtc()
    {
        var line = new StringBuilder();
        for (int i = 0; i < ndata; i++)
        {
            line.Append(data[i] switch
            {
                (byte)'\t' => @" \t",
                (byte)'\r' => @" \r",
                (byte)'\n' => @" \n",
                (byte)'\b' => @" \b",
                >= 0x7F or < (byte)' ' => " " + Number(data[i], 2, 2),
                _ => "  " + (char)data[i],
            });
        }

        return line.ToString();
    }

    // xprint, whose Bvprint cannot fail: a failed write is fatal before it returns.
    private async Task XprintAsync(string s) => await bout.BwriteAsync(Encoding.UTF8.GetBytes(s));

    private async Task SysfatalAsync(string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes(message + "\n"));
        await ExitsAsync(message);
    }

    // exits, after libbio's atexit has flushed standard output.
    private async Task ExitsAsync(string? status)
    {
        await bout.BflushAsync();
        process.Exits(status);
    }

    // A format: ascii or a length of 1, 2, 4 or 8 bytes, in base 8, 10 or 16, and how its address
    // is printed: at least 7 digits for the first format, or padded to 7 for those after it, in
    // the base -a gave last before it.
    private sealed class Arg
    {
        public bool Ascii { get; set; }

        public int Loglen { get; set; } = 2;

        public int Base { get; set; } = 2;

        public (bool Second, int Base) Afmt { get; set; }
    }
}
