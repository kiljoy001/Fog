using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// libc's tmtimens, tmfmt and tmnorm against 9front's own: each line of Data/tm/tm.in, its seconds,
// nanoseconds and format, laid out in GMT and in zones of /env/timezone as 9front's tmfmt laid it
// out, with what tmnorm made of it, in Data/tm/<zone>.out; a line with fields to set has them set
// first, and is laid out after tmnorm.
public sealed class TmTests
{
    public static TheoryData<string, string?> Zones => new()
    {
        { "gmt", null },
        { "xst", "XST -18000 XDT -14400\n 1000000000 1100000000\n" },
        { "ist", "IST 19800 IST 19800\n" },
        { "nst", "NST -12600 NDT -9000\n 1000000000 1100000000 1200000000\n" },
        { "xst", "\n\t XST -18000 XDT -14400\n 1000000000 1100000000\n" },
    };

    // tzload: a name other than local is a file of /adm/timezone, and one of 32 bytes or more is too
    // long to be one.
    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task TzloadReadsANamedZoneFromAdmAndRefusesALongName()
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        Tm.Tzone gmt = (await Tm.TzloadAsync(init, "GMT"))!;
        Assert.Equal(("GMT", 0, "GMT", 0, 0), (gmt.Stname, gmt.Stdiff, gmt.Dlname, gmt.Dldiff, gmt.Dlpairs.Count));

        Assert.Null(await Tm.TzloadAsync(init, new string('x', 32)));
        Assert.Equal("timezone name too long", Errstr.Get(init));
        Assert.Null(await Tm.TzloadAsync(init, new string('x', 31)));
        Assert.StartsWith("file does not exist", Errstr.Get(init), StringComparison.Ordinal);
    }

    // A zone's file of 1653 bytes is read whole, its last pair of transitions with it, as 9front's
    // tzload read it; one as long as tzload's buffer, 1680 bytes, is no zone, and errstr is left.
    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task TzloadReadsAZoneShorterThanItsBuffer()
    {
        string pairs = string.Concat(Enumerable.Range(1, 148).Select(i => $"{1000000000 + i}\n"));
        Tm.Tzone zone = (await LocalAsync($"ABC 0 XYZ 3600          \n{pairs}"))!;
        Assert.Equal("XYZ", Encoding.UTF8.GetString(Tm.Tmtimens(1000000148, 0, zone).Format("ZZZ", out _)!));
        Assert.Equal("ABC", Encoding.UTF8.GetString(Tm.Tmtimens(1000000147, 0, zone).Format("ZZZ", out _)!));
        Assert.Null(await LocalAsync($"ABC 0 XYZ 3600{new string(' ', 41)}\n{pairs}"));
    }

    // A format that ends in underscores ends there; 9front's reads on past its NUL.
    [Fact]
    public void AFormatEndsAtItsTrailingUnderscores()
    {
        Assert.Equal("2001", Encoding.UTF8.GetString(Tm.Tmtimens(1000000000, 0, null).Format("YYYY__", out _)!));
        Assert.Equal(string.Empty, Encoding.UTF8.GetString(Tm.Tmtimens(1000000000, 0, null).Format("_", out _)!));
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [MemberData(nameof(Zones))]
    public async Task FormatsAndNormalizesAs9frontDoes(string zone, string? timezone)
    {
        Tm.Tzone? tz = null;
        if (timezone is not null)
        {
            tz = await LocalAsync(timezone);
            Assert.NotNull(tz);
        }

        string data = Path.Combine(AppContext.BaseDirectory, "Data", "tm");
        string[] expected = await File.ReadAllLinesAsync(Path.Combine(data, $"{zone}.out"));
        string[] input = await File.ReadAllLinesAsync(Path.Combine(data, "tm.in"));
        Assert.Equal(input.Length, expected.Length);
        for (int i = 0; i < input.Length; i++)
        {
            string[] f = input[i].Split('\t');
            Tm tm = Tm.Tmtimens(long.Parse(f[0], CultureInfo.InvariantCulture), long.Parse(f[1], CultureInfo.InvariantCulture), tz);
            if (f.Length < 4)
            {
                string text = Encoding.UTF8.GetString(tm.Format(f[2], out _)!);
                Assert.Equal(expected[i], $"[{text}] {tm.Tmnorm()}");
                continue;
            }

            int[] v = [.. f[3].Split(' ').Select(x => int.Parse(x, CultureInfo.InvariantCulture))];
            (tm.Sec, tm.Min, tm.Hour, tm.Mday, tm.Mon, tm.Year, tm.Nanosec) = (v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
            long t = tm.Tmnorm();
            Assert.Equal(expected[i], $"[{Encoding.UTF8.GetString(tm.Format(f[2], out _)!)}] {t}");
        }
    }

    // tzload("local") of a booted kernel whose /env/timezone holds the zone.
    private static async Task<Tm.Tzone?> LocalAsync(string timezone)
    {
        Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        int fd = await init.CreateAsync("/env/timezone", NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(fd, Encoding.UTF8.GetBytes(timezone));
        await init.CloseAsync(fd);
        return await Tm.TzloadAsync(init, "local");
    }
}
