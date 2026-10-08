using System.Text;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Specifications;
using Microsoft.Extensions.Time.Testing;
using NinePSharp.Constants;
using NinePSharp.Fog.Coyote.Tests.Support;
using NinePSharp.Fog.Gefs;
using NinePSharp.Fog.Gefs.Tests.Support;
using NinePSharp.Namespaces;
using Reqnroll;
using Reqnroll.UnitTestProvider;

namespace NinePSharp.Fog.Coyote.Tests.Steps;

[Binding]
[Scope(Feature = "A gefs store's one writer keeps its trees whole in every interleaving Coyote explores")]
public sealed class GefsInterleavingSteps(IUnitTestRuntimeProvider runtime, IReqnrollOutputHelper output)
{
    private Func<Task>? race;
    private ulong sequence;

    [Given("a store serving main and work, a fork of main")]
    [Given("a store with main snapshotted as monday")]
    [Given(@"^a store serving main, with notes holding ""first"" committed$")]
    public void GivenStore()
    {
        if (!RewritingEngine.IsAssemblyRewritten(typeof(GefsServer).Assembly))
        {
            runtime.TestIgnore("the Fog assemblies were not rewritten with coyote rewrite");
        }
    }

    [When("2 writers each create a file in main and 2 in work while another task commits")]
    public void WhenWritersRaceCommit() => race = async () =>
    {
        var (device, server) = Ream();
        server.Snap("main", "work", true);
        GefsFs[] trees = [server.Attach("main"), server.Attach("work")];
        Task[] tasks = [.. Enumerable.Range(0, 4).Select(i => Task.Run(() => WriteAsync(trees[i / 2], $"f{i}", $"file {i}", create: true))), Task.Run(server.Sync)];
        await Task.WhenAll(tasks);
        server.Dispose();

        using GefsServer reopened = Reopen(device, out Store store);
        foreach (var (label, first) in new[] { ("main", 0), ("work", 2) })
        {
            GefsFs fs = reopened.Attach(label);
            string[] names = (await fs.ReadDirectoryAsync(fs.Root, default)).Select(e => e.Name).ToArray();
            Specification.Assert(names.SequenceEqual(new[] { $"f{first}", $"f{first + 1}" }), $"{label} holds {string.Join(", ", names)}");
            for (int i = first; i < first + 2; i++)
            {
                string text = await ReadAsync(fs, $"f{i}");
                Specification.Assert(text == $"file {i}", $"{label}'s f{i} holds \"{text}\"");
            }
        }

        Accounting.Check(store);
    };

    [When("2 tasks attach to monday at once")]
    public void WhenAttachesRace() => race = async () =>
    {
        var (_, server) = Ream();
        server.Snap("main", "monday", false);
        GefsFs[] served = await Task.WhenAll(Task.Run(() => server.Attach("monday")), Task.Run(() => server.Attach("monday")));
        Specification.Assert(ReferenceEquals(served[0], served[1]), "two attaches to monday served two trees");
        server.Dispose();
    };

    [When(@"^a writer writes ""secnd"" to notes while another task snapshots main as monday and a third commits$")]
    public void WhenWriteRacesSnapshot() => race = async () =>
    {
        var (device, server) = Ream();
        GefsFs main = server.Attach("main");
        await WriteAsync(main, "notes", "first", create: true);
        server.Sync();
        await Task.WhenAll(Task.Run(() => WriteAsync(main, "notes", "secnd", create: false)), Task.Run(() => server.Snap("main", "monday", false)), Task.Run(server.Sync));
        server.Dispose();

        using GefsServer reopened = Reopen(device, out Store store);
        string before = await ReadAsync(reopened.Attach("monday"), "notes");
        string after = await ReadAsync(reopened.Attach("main"), "notes");
        Specification.Assert(before is "first" or "secnd", $"monday's notes hold \"{before}\"");
        Specification.Assert(after == "secnd", $"main's notes hold \"{after}\"");
        Accounting.Check(store);
    };

    [Then("in every explored schedule, once the device is opened again, main and work each hold their writers' files with what was written, and every block of the device is held, in a log or free")]
    [Then("in every explored schedule both attaches serve the same tree")]
    [Then(@"^in every explored schedule, once the device is opened again, monday's notes hold ""first"" or ""secnd"", main's hold ""secnd"", and every block of the device is held, in a log or free$")]
    public void ThenExplored() => Explorer.Explore(race!, output, memoryAccesses: true);

    private static (MemoryDevice Device, GefsServer Server) Ream()
    {
        var device = new MemoryDevice(800);
        var clock = new FakeTimeProvider();
        return (device, new GefsServer(device, Store.Ream(device, 2, "adm", clock), (user, group) => user == group, clock));
    }

    private static GefsServer Reopen(MemoryDevice device, out Store store)
    {
        store = Store.Open(device);
        var clock = new FakeTimeProvider();
        return new GefsServer(device, store, (user, group) => user == group, clock);
    }

    // Coyote rewrites awaits of the file server's value tasks, but not other uses of them.
    private async Task<string> ReadAsync(GefsFs fs, string name)
    {
        ResourceHandle file = (await fs.WalkAsync(fs.Root, name, default))!;
        ResourceOpenHandle open = await fs.OpenAsync(file, NinePConstants.OREAD, Context(), default);
        ReadOnlyMemory<byte> data = await fs.ReadAsync(open, 0, 100, default);
        await fs.ClunkAsync(open, Context(), default);
        return Encoding.ASCII.GetString(data.Span);
    }

    private async Task WriteAsync(GefsFs fs, string name, string text, bool create)
    {
        ResourceOpenHandle open;
        if (create)
        {
            open = await fs.CreateAndOpenAsync(fs.Root, name, 0b110_100_100, NinePConstants.OWRITE, Context(), default);
        }
        else
        {
            ResourceHandle file = (await fs.WalkAsync(fs.Root, name, default))!;
            open = await fs.OpenAsync(file, NinePConstants.OWRITE, Context(), default);
        }

        await fs.WriteAsync(open, 0, Encoding.ASCII.GetBytes(text), Context(), default);
        await fs.ClunkAsync(open, Context(), default);
    }

    private ResourceOperationContext Context() => new(new ResourceOperationId("coyote", Interlocked.Increment(ref sequence)), 1, "adm");
}
