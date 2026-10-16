using System.Text;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Specifications;
using NinePSharp.Constants;
using NinePSharp.Fog.Coyote.Tests.Support;
using NinePSharp.Fog.Kernel;
using Reqnroll;
using Reqnroll.UnitTestProvider;

namespace NinePSharp.Fog.Coyote.Tests.Steps;

[Binding]
[Scope(Feature = "Notes reach a process once in every interleaving Coyote explores")]
public sealed class NoteInterleavingSteps(IUnitTestRuntimeProvider runtime, IReqnrollOutputHelper output)
{
    private Func<Child, Task>? race;

    [Given("a kernel whose child, which continues after every note, reads a pipe")]
    public void GivenKernel()
    {
        if (!RewritingEngine.IsAssemblyRewritten(typeof(FogKernel).Assembly))
        {
            runtime.TestIgnore("the Fog assemblies were not rewritten with coyote rewrite");
        }
    }

    [When(@"^the parent writes ""(.*)"" to the pipe while another task posts ""(.*)"" to the child$")]
    public void WhenWriteRacesNote(string data, string note) => race = async child =>
    {
        await Task.WhenAll(Task.Run(() => child.WriteAsync(data)), Task.Run(() => child.PostAsync(note)));
        await child.CloseAsync();
    };

    [When(@"^2 tasks each post a note to the child at once, and then the parent writes ""(.*)"" to the pipe$")]
    public void WhenNotesRace(string data) => race = async child =>
    {
        await Task.WhenAll(Task.Run(() => child.PostAsync("hangup")), Task.Run(() => child.PostAsync("alarm")));
        await child.WriteAsync(data);
        await child.CloseAsync();
    };

    [When(@"^the parent closes the pipe while another task posts ""(.*)"" to the child$")]
    public void WhenCloseRacesNote(string note) => race = async child =>
        await Task.WhenAll(Task.Run(child.CloseAsync), Task.Run(() => child.PostAsync(note)));

    [Then(@"^in every explored schedule the child reads ""(.*)"", at once or after its read fails ""interrupted""$")]
    public void ThenReads(string data) => Explore(child =>
        Specification.Assert(child.Read == data, $"the child read \"{child.Read}\""));

    [Then(@"^in every explored schedule the child's handler saw ""(.*)"" once$")]
    public void ThenSawOnce(string note) => Explore(child =>
        Specification.Assert(child.Seen.Count(seen => seen == note) == 1, "the child saw " + string.Join(", ", child.Seen)));

    [Then("in every explored schedule the child's handler saw each note once")]
    public void ThenSawEach() => Explore(child =>
        Specification.Assert(child.Seen.Order(StringComparer.Ordinal).SequenceEqual(["alarm", "hangup"]), "the child saw " + string.Join(", ", child.Seen)));

    [Then("in every explored schedule the note is posted or refused as the child has exited, and the child ends")]
    public void ThenPostedOrRefused() => Explore(child =>
    {
        string posted = child.Posts.Single();
        Specification.Assert(posted is "posted" or Errors.NoteNotPosted or Errors.ProcessExited || posted.StartsWith(Errors.NotExist, StringComparison.Ordinal), $"the post gave {posted}");
        Specification.Assert(posted != "posted" || child.Seen.Count <= 1, "the child saw " + string.Join(", ", child.Seen));
    });

    // Whatever the rule, a read is interrupted only by a note that was posted.
    private void Explore(Action<Child> rule) => Explorer.Explore(
        async () =>
        {
            Child child = await Child.StartAsync();
            await race!(child);
            await child.EndAsync();
            int posted = child.Posts.Count(post => post == "posted");
            Specification.Assert(child.Interruptions <= posted, $"{child.Interruptions} reads were interrupted by {posted} notes");
            rule(child);
        },
        output);

    // A child that reads its end of a pipe to the end, reading again when a note interrupts it, then
    // makes as many calls as notes can wait, so each note posted while it ran is delivered.
    private sealed class Child
    {
        private readonly List<string> seen = [];
        private readonly StringBuilder read = new();
        private readonly List<string> posts = [];
        private Process parent = null!;
        private int write;
        private long pid;

        public int Interruptions { get; private set; }

        public string Read => read.ToString();

        public IReadOnlyList<string> Seen
        {
            get
            {
                lock (seen)
                {
                    return [.. seen];
                }
            }
        }

        public IReadOnlyList<string> Posts
        {
            get
            {
                lock (posts)
                {
                    return [.. posts];
                }
            }
        }

        public static async Task<Child> StartAsync()
        {
            var child = new Child { parent = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync() };
            var (readEnd, writeEnd) = await child.parent.PipeAsync();
            child.write = writeEnd;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            child.pid = child.parent.Fork(RforkFlags.Fdg, async process =>
            {
                process.Notify(note =>
                {
                    lock (child.seen)
                    {
                        child.seen.Add(note);
                    }

                    return true;
                });
                await process.CloseAsync(writeEnd);
                started.SetResult(true);
                await child.ReadAllAsync(process, readEnd);
            });
            await child.parent.CloseAsync(readEnd);
            await started.Task;
            return child;
        }

        public Task WriteAsync(string data) => parent.WriteAsync(write, Encoding.UTF8.GetBytes(data)).AsTask();

        public Task CloseAsync() => parent.CloseAsync(write).AsTask();

        public async Task PostAsync(string note)
        {
            string result;
            try
            {
                int fd = await parent.OpenAsync($"/proc/{pid}/note", NinePConstants.OWRITE);
                try
                {
                    await parent.WriteAsync(fd, Encoding.UTF8.GetBytes(note));
                    result = "posted";
                }
                finally
                {
                    await parent.CloseAsync(fd);
                }
            }
            catch (SyscallException error)
            {
                result = error.Message;
            }

            lock (posts)
            {
                posts.Add(result);
            }
        }

        public async Task EndAsync() => await parent.WaitAsync();

        private async Task ReadAllAsync(Process process, int fd)
        {
            while (true)
            {
                ReadOnlyMemory<byte> block;
                try
                {
                    block = await process.ReadAsync(fd, 64);
                }
                catch (SyscallException error) when (error.Message == Errors.Interrupted)
                {
                    // More interruptions than notes can wait is a spin; the rule reports it.
                    if (++Interruptions > Process.NoteMax)
                    {
                        break;
                    }

                    continue;
                }

                if (block.IsEmpty)
                {
                    break;
                }

                read.Append(Encoding.UTF8.GetString(block.Span));
            }

            for (int i = 0; i < Process.NoteMax; i++)
            {
                await process.StatAsync("/");
            }
        }
    }
}
