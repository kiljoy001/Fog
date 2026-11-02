using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// 9front's own tests of diff, from /sys/src/cmd/diff/test, as its diff.rc runs them: diff -u of
// each diff-t*.l and diff-t*.r it does not leave out as broken, the same as diff-t*.expected.
public sealed class DiffSuiteTests : IAsyncLifetime
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Data", "diff");
    private SecstoreHarness h = null!;

    public static TheoryData<int> Tests => [1, 2, 3, 4, 5, 6, 7, 11, 12, 13, 15];

    public async Task InitializeAsync()
    {
        h = await SecstoreHarness.StartAsync(rc: true);
        await h.MkdirAsync("/tmp/diff");
        foreach (string f in Directory.GetFiles(Dir))
        {
            await h.WriteFileAsync($"/tmp/diff/{Path.GetFileName(f)}", await File.ReadAllBytesAsync(f));
        }
    }

    public async Task DisposeAsync() => await h.DisposeAsync();

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Tests))]
    public async Task DiffUPrintsWhatIsExpected(int t)
    {
        (string output, _) = await h.RunInAsync("/tmp/diff", string.Empty, ["diff", "-u", $"diff-t{t}.l", $"diff-t{t}.r"]);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Dir, $"diff-t{t}.expected")), output);
    }
}
