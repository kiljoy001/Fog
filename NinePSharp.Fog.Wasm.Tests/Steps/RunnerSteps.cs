using System.Text;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Wasm.Tests.Steps;

// fog-wasi runs in-process, on a host directory and a copy of the fixture outside it, with its
// stdin, stdout and stderr in memory.
[Binding]
public sealed class RunnerSteps : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DateTime Written = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("fog-wasi-");
    private string host = string.Empty;
    private string module = string.Empty;
    private int exitCode;
    private string stdout = string.Empty;
    private string stderr = string.Empty;

    private string Outside => Path.Combine(scratch.FullName, "outside");

    [Given("a host directory")]
    public void GivenHostDirectory() => host = scratch.CreateSubdirectory("host").FullName;

    [Given(@"^the host directory holds ""(.*)"" with ""(.*)""$")]
    public void GivenHostFile(string name, string contents) => File.WriteAllText(Path.Combine(host, name), contents);

    [Given(@"^the host directory holds the directory ""(.*)""$")]
    public void GivenHostDirectory(string name) => Directory.CreateDirectory(Path.Combine(host, name));

    [Given(@"^the host directory holds a symbolic link ""(.*)"" to ""(.*)""$")]
    public void GivenLink(string name, string target) => File.CreateSymbolicLink(Path.Combine(host, name), target);

    [Given(@"^the host directory holds a symbolic link ""(.*)"" to a file outside it holding ""(.*)""$")]
    public void GivenLinkOutside(string name, string contents)
    {
        File.WriteAllText(Outside, contents);
        File.CreateSymbolicLink(Path.Combine(host, name), Outside);
    }

    [Given(@"^the host directory holds the read-only file ""(.*)"" with ""(.*)""$")]
    public void GivenReadOnlyFile(string name, string contents)
    {
        string path = Path.Combine(host, name);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, Written);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    [When(@"^fog-wasi runs with the arguments ""(.*)""$")]
    public async Task WhenRunBare(string arguments)
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        exitCode = await WasiRunner.RunAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), new MemoryStream(), output, error).WaitAsync(Bound);
        stderr = Encoding.UTF8.GetString(error.ToArray());
    }

    [When(@"^fog-wasi runs the module ""([^""]*)""$")]
    public Task WhenRun(string fixture) => RunAsync(fixture, [], [], string.Empty);

    [When(@"^fog-wasi runs the module ""(.*)"" with the arguments ""(.*)""$")]
    public Task WhenRunWithArguments(string fixture, string arguments) => RunAsync(fixture, [], arguments.Split(' '), string.Empty);

    [When(@"^fog-wasi runs the module ""(.*)"" with the options ""(.*)""$")]
    public Task WhenRunWithOptions(string fixture, string options) => RunAsync(fixture, options.Split(' '), [], string.Empty);

    [When("fog-wasi runs the trace interpreter on the host directory with the script:")]
    public Task WhenRunTrace(string script) => RunAsync("trace", ["--dir", $"{host}::/"], [], script + "\n");

    [Then(@"^fog-wasi exits with (\d+)$")]
    public void ThenExits(int code) => Assert.Equal(code, exitCode);

    [Then(@"^its stdout is ""(.*)""$")]
    public void ThenStdout(string expected) => Assert.Equal(Unescape(expected).Replace("<module>", module, StringComparison.Ordinal), stdout);

    [Then("its stdout is:")]
    public void ThenStdoutLines(string expected) => Assert.Equal(expected + "\n", stdout);

    [Then(@"^its stderr is ""(.*)""$")]
    public void ThenStderr(string expected) => Assert.Equal(Unescape(expected), stderr);

    [Then(@"^the host file ""(.*)"" was not written$")]
    public void ThenNotWritten(string name) => Assert.Equal(Written, File.GetLastWriteTimeUtc(Path.Combine(host, name)));

    [Then("the host directory holds only:")]
    public void ThenHostHolds(DataTable table)
    {
        var expected = table.Rows.Select(row => (row["name"], row["contents"]));
        var actual = new DirectoryInfo(host).EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal).Select(entry =>
            (entry.Name, entry.LinkTarget is { } target ? $"<link {target}>"
                : entry is DirectoryInfo directory ? (directory.EnumerateFileSystemInfos().Any() ? "<nonempty directory>" : "<directory>")
                : File.ReadAllText(entry.FullName)));
        Assert.Equal(expected, actual);
    }

    [Then(@"^the file outside the host directory holds ""(.*)""$")]
    public void ThenOutside(string contents) => Assert.Equal(contents, File.ReadAllText(Outside));

    public void Dispose() => scratch.Delete(true);

    private static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal);

    private async Task RunAsync(string fixture, string[] options, string[] arguments, string input)
    {
        module = Path.Combine(scratch.FullName, fixture + ".wasm");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".wasm"), module);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        exitCode = await WasiRunner.RunAsync([.. options, module, .. arguments], new MemoryStream(Encoding.UTF8.GetBytes(input)), output, error).WaitAsync(Bound);
        stdout = Encoding.UTF8.GetString(output.ToArray());
        stderr = Encoding.UTF8.GetString(error.ToArray());
    }
}
