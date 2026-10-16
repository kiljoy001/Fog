using System.Buffers.Binary;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Specifications;
using NinePSharp.Constants;
using NinePSharp.Fog.Coyote.Tests.Support;
using NinePSharp.Fog.Kernel;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using Reqnroll;
using Reqnroll.UnitTestProvider;

namespace NinePSharp.Fog.Coyote.Tests.Steps;

[Binding]
[Scope(Feature = "A mount's connection hands every reply to its caller in every interleaving Coyote explores")]
public sealed class MountInterleavingSteps(IUnitTestRuntimeProvider runtime, IReqnrollOutputHelper output)
{
    private const string RpcError = "mount rpc error";
    private Func<Mount, Task<Dictionary<string, string>>>? race;

    [Given("a mount connection to a server that holds its replies")]
    public void GivenConnection()
    {
        if (!RewritingEngine.IsAssemblyRewritten(typeof(FogKernel).Assembly))
        {
            runtime.TestIgnore("the Fog assemblies were not rewritten with coyote rewrite");
        }
    }

    [When("3 callers each stat a different file while another task answers them in reverse order")]
    public void WhenAnsweredInReverse() => race = async mount =>
    {
        Task<string>[] callers = [.. new uint[] { 1, 2, 3 }.Select(fid => Task.Run(() => mount.StatAsync(fid)))];
        await Task.Run(async () =>
        {
            foreach (Request request in (await mount.Server.RequestsAsync(3)).Reverse())
            {
                mount.Server.Answer(request);
            }
        });
        string[] stats = await Task.WhenAll(callers);
        return new() { ["1"] = stats[0], ["2"] = stats[1], ["3"] = stats[2] };
    };

    [When("2 callers each stat a file, one of them cancelled while another task answers both")]
    public void WhenOneCancelled() => race = async mount =>
    {
        using var cancel = new CancellationTokenSource();
        Task<string> cancelled = Task.Run(() => mount.StatAsync(1, cancel.Token));
        Task<string> other = Task.Run(() => mount.StatAsync(2));
        Task canceller = Task.Run(cancel.Cancel);
        await Task.Run(() => mount.AnswerAsync(2));
        await canceller;
        var stats = new Dictionary<string, string> { ["1"] = await cancelled, ["2"] = await other };
        Task<string> later = Task.Run(() => mount.StatAsync(4));
        await Task.Run(async () => mount.Server.Answer((await mount.Server.RequestsAsync(3))[2]));
        stats["later"] = await later;
        return stats;
    };

    [When("2 callers each stat a file while another task answers one of them and the server hangs up")]
    public void WhenHungUp() => race = async mount =>
    {
        Task<string>[] callers = [Task.Run(() => mount.StatAsync(1)), Task.Run(() => mount.StatAsync(2))];
        await Task.Run(async () =>
        {
            mount.Server.Answer((await mount.Server.RequestsAsync(2))[0]);
            mount.Server.HangUp();
        });
        string[] stats = await Task.WhenAll(callers);
        return new() { ["1"] = stats[0], ["2"] = stats[1], ["later"] = await mount.StatAsync(4) };
    };

    [When(@"^2 callers each stat a file while another task refuses one with ""(.*)"" and answers the other$")]
    public void WhenRefused(string error) => race = async mount =>
    {
        Task<string>[] callers = [Task.Run(() => mount.StatAsync(1)), Task.Run(() => mount.StatAsync(2))];
        await Task.Run(async () =>
        {
            foreach (Request request in await mount.Server.RequestsAsync(2))
            {
                if (request.Fid == 1)
                {
                    mount.Server.Refuse(request, error);
                }
                else
                {
                    mount.Server.Answer(request);
                }
            }
        });
        string[] stats = await Task.WhenAll(callers);
        return new() { ["1"] = stats[0], ["2"] = stats[1] };
    };

    [Then("in every explored schedule each caller gets the stat of its own file")]
    public void ThenOwnStats() => Explore((_, stats) =>
    {
        foreach (string caller in new[] { "1", "2", "3" })
        {
            Specification.Assert(stats[caller] == "f" + caller, $"caller {caller} got {stats[caller]}");
        }
    });

    [Then("in every explored schedule no tag is used by two requests at once")]
    public void ThenTagsUnique() => Explore((server, _) =>
        Specification.Assert(server.Reused.Count == 0, "tags reused while in flight: " + string.Join(", ", server.Reused)));

    [Then("in every explored schedule the cancelled caller gets its stat or is cancelled, and is cancelled only after the server has its flush")]
    public void ThenCancelledOrStat() => Explore((_, stats) =>
        Specification.Assert(stats["1"] is "f1" or "cancelled", $"the cancelled caller got {stats["1"]}"));

    [Then("in every explored schedule the other caller gets the stat of its own file")]
    public void ThenOtherStat() => Explore((_, stats) =>
        Specification.Assert(stats["2"] == "f2", $"the other caller got {stats["2"]}"));

    [Then("in every explored schedule a later stat on the connection gets its reply")]
    public void ThenLaterStat() => Explore((_, stats) =>
        Specification.Assert(stats["later"] == "f4", $"the later stat got {stats["later"]}"));

    [Then(@"^in every explored schedule each caller gets the stat of its own file or fails with ""mount rpc error""$")]
    public void ThenStatOrFailed() => Explore((_, stats) =>
    {
        foreach (string caller in new[] { "1", "2" })
        {
            Specification.Assert(stats[caller] == "f" + caller || stats[caller] == RpcError, $"caller {caller} got {stats[caller]}");
        }
    });

    [Then(@"^in every explored schedule a later stat on the connection fails with ""mount rpc error""$")]
    public void ThenLaterFailed() => Explore((_, stats) =>
        Specification.Assert(stats["later"] == RpcError, $"the later stat got {stats["later"]}"));

    [Then(@"^in every explored schedule the refused caller fails with ""(.*)""$")]
    public void ThenRefused(string error) => Explore((_, stats) =>
        Specification.Assert(stats["1"] == error, $"the refused caller got {stats["1"]}"));

    private void Explore(Action<Server, Dictionary<string, string>> rule) => Explorer.Explore(
        async () =>
        {
            Mount mount = await Mount.OpenAsync();
            Dictionary<string, string> stats = await race!(mount);
            rule(mount.Server, stats);
        },
        output);

    public readonly record struct Request(ushort Tag, uint Fid);

    private sealed class Mount(Server server, MountConnection connection)
    {
        private static readonly ResourceOperationContext Context = new(new ResourceOperationId("coyote", 1), 1, "glenda");

        public Server Server { get; } = server;

        public static async Task<Mount> OpenAsync()
        {
            var server = new Server();
            var connection = new MountConnection(server, server.Handle, server, () => Context);
            await connection.VersionAsync();
            return new Mount(server, connection);
        }

        // The stat's name, the call's error, or that it was cancelled: only once the server has had
        // its flush, as mountio waits for the flush before the call ends.
        public async Task<string> StatAsync(uint fid, CancellationToken cancellationToken = default)
        {
            try
            {
                byte[] reply = await connection.RpcAsync(tag => new Tstat(tag, fid), MessageTypes.Rstat, cancellationToken);
                return new Rstat(reply).Stat.Name;
            }
            catch (OperationCanceledException)
            {
                return Server.Flushed(fid) ? "cancelled" : "cancelled before its flush";
            }
            catch (IOException error)
            {
                return error.Message;
            }
        }

        public async Task AnswerAsync(int count)
        {
            foreach (Request request in await Server.RequestsAsync(count))
            {
                Server.Answer(request);
            }
        }
    }

    // A 9P server over an in-memory transport: Tversion and Tflush are answered at once, a flushed
    // request is never answered after its Rflush, and a Tstat waits for the scenario to answer it.
    private sealed class Server : IResourceDataOperations, IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly List<Request> arrived = [];
        private readonly Dictionary<ushort, Request> pending = [];
        private readonly HashSet<uint> flushed = [];
        private byte[] outbound = [];
        private bool hungUp;
        private TaskCompletionSource<bool> changed = NewSignal();

        public ResourceOpenHandle Handle { get; } = new(new ResourceHandle(new ResourceIdentity("coyote", "#|", 0), QidType.QTFILE), "transport", NinePConstants.ORDWR, 0);

        public List<string> Reused { get; } = [];

        public bool Flushed(uint fid)
        {
            lock (gate)
            {
                return flushed.Contains(fid);
            }
        }

        public async Task<Request[]> RequestsAsync(int count)
        {
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (arrived.Count >= count)
                    {
                        return [.. arrived.Take(count)];
                    }

                    wait = changed.Task;
                }

                await wait;
            }
        }

        public void Answer(Request request)
        {
            var stat = new Stat(0, 0, 0, new Qid(QidType.QTFILE, 0, request.Fid), 0b110_100_100, 0, 0, 0, $"f{request.Fid}", "glenda", "glenda", "glenda");
            Reply(request, new Rstat(request.Tag, stat));
        }

        public void Refuse(Request request, string error) => Reply(request, new Rerror(request.Tag, error));

        public void HangUp()
        {
            TaskCompletionSource<bool> signal;
            lock (gate)
            {
                hungUp = true;
                signal = Swap();
            }

            signal.TrySetResult(true);
        }

        public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
        {
            byte[] message = data.ToArray();
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(5, 2));
            TaskCompletionSource<bool> signal;
            lock (gate)
            {
                if (hungUp)
                {
                    throw new IOException("i/o on hungup channel");
                }

                switch ((MessageTypes)message[4])
                {
                    case MessageTypes.Tversion:
                        Send(new Rversion(tag, new Tversion(message).MSize, "9P2000"));
                        break;
                    case MessageTypes.Tflush:
                        Arrive(tag);
                        ushort old = new Tflush(message).OldTag;
                        if (arrived.FindLast(request => request.Tag == old) is { Fid: > 0 } flushing)
                        {
                            flushed.Add(flushing.Fid);
                        }

                        pending.Remove(old);
                        Send(new Rflush(tag));
                        break;
                    default:
                        Arrive(tag);
                        var request = new Request(tag, new Tstat(message).Fid);
                        pending[tag] = request;
                        arrived.Add(request);
                        break;
                }

                signal = Swap();
            }

            signal.TrySetResult(true);
            return ValueTask.FromResult((uint)message.Length);
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
            => new(ReadAsync(count));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A Task, wrapped as the ValueTask the device returns, so a Coyote-rewritten caller controls the wait.
        private async Task<ReadOnlyMemory<byte>> ReadAsync(uint count)
        {
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (outbound.Length != 0)
                    {
                        int length = Math.Min(outbound.Length, (int)count);
                        byte[] read = outbound[..length];
                        outbound = outbound[length..];
                        return read;
                    }

                    if (hungUp)
                    {
                        return ReadOnlyMemory<byte>.Empty;
                    }

                    wait = changed.Task;
                }

                await wait;
            }
        }

        private void Arrive(ushort tag)
        {
            if (pending.ContainsKey(tag))
            {
                Reused.Add($"{tag}");
            }
        }

        private void Reply(Request request, ISerializable reply)
        {
            TaskCompletionSource<bool> signal;
            lock (gate)
            {
                if (!pending.Remove(request.Tag))
                {
                    return;
                }

                Send(reply);
                signal = Swap();
            }

            signal.TrySetResult(true);
        }

        private void Send(ISerializable reply)
        {
            byte[] bytes = new byte[reply.Size];
            reply.WriteTo(bytes);
            outbound = [.. outbound, .. bytes];
        }

        // The signal that wakes whoever waits now, to be set once the lock is let go.
        private TaskCompletionSource<bool> Swap()
        {
            TaskCompletionSource<bool> signal = changed;
            changed = NewSignal();
            return signal;
        }
    }
}
