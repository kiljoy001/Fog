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
// out, with what tmnorm made of it, in Data/tm/<zone>.out.
public sealed class TmTests
{
    public static TheoryData<string, string?> Zones => new()
    {
        { "gmt", null },
        { "xst", "XST -18000 XDT -14400\n 1000000000 1100000000\n" },
        { "ist", "IST 19800 IST 19800\n" },
        { "nst", "NST -12600 NDT -9000\n 1000000000 1100000000 1200000000\n" },
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

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [MemberData(nameof(Zones))]
    public async Task FormatsAndNormalizesAs9frontDoes(string zone, string? timezone)
    {
        Tm.Tzone? tz = null;
        if (timezone is not null)
        {
            Process init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
            int fd = await init.CreateAsync("/env/timezone", NinePConstants.OWRITE, 0b110_110_110);
            await init.WriteAsync(fd, Encoding.UTF8.GetBytes(timezone));
            await init.CloseAsync(fd);
            tz = await Tm.TzloadAsync(init, "local");
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
            string text = Encoding.UTF8.GetString(tm.Format(f[2], out _)!);
            Assert.Equal(expected[i], $"[{text}] {tm.Tmnorm()}");
        }
    }
}
