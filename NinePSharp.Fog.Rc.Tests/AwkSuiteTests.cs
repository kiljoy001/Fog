using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// 9front's own tests of awk, from /sys/src/cmd/awk/test, as its awk.rc runs them: each p.* on
// test.countries twice, and each t.* that is not .broken on test.data, its output and errors
// together the same as the expected.* 9front's awk printed.
public sealed class AwkSuiteTests : IAsyncLifetime
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Data", "awk");
    private SecstoreHarness h = null!;

    public static TheoryData<string> Programs()
    {
        var data = new TheoryData<string>();
        foreach (string f in Directory.GetFiles(Dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!)
        {
            if (f!.StartsWith("p.", StringComparison.Ordinal) || f.StartsWith("t.", StringComparison.Ordinal))
            {
                data.Add(f);
            }
        }

        return data;
    }

    public async Task InitializeAsync()
    {
        h = await SecstoreHarness.StartAsync(rc: true);
        await h.MkdirAsync("/tmp/awk");
        foreach (string f in Directory.GetFiles(Dir).Where(f => !Path.GetFileName(f).StartsWith("expected.", StringComparison.Ordinal)))
        {
            await h.WriteFileAsync($"/tmp/awk/{Path.GetFileName(f)}", await File.ReadAllBytesAsync(f));
        }
    }

    public async Task DisposeAsync() => await h.DisposeAsync();

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Programs))]
    public async Task ProgramPrintsWhat9frontsAwkPrinted(string program)
    {
        string[] data = program.StartsWith("p.", StringComparison.Ordinal) ? ["test.countries", "test.countries"] : ["test.data"];
        (string output, _) = await h.RunInAsync("/tmp/awk", string.Empty, ["awk", "-f", program, .. data]);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Dir, $"expected.{program}")), output);
        if (program == "p.47")
        {
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Dir, "expected.p.47.tempbig")), await h.FileAsync("/tmp/awk/foo.tempbig"));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Dir, "expected.p.47.tempsmall")), await h.FileAsync("/tmp/awk/foo.tempsmall"));
        }
    }
}
