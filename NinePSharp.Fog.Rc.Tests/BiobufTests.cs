using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Messages;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// libbio's Bread, Bwrite and Bflush: what each gives back, what Berror is told, and when a
// buffer stops taking writes or reading.
public sealed class BiobufTests : IAsyncLifetime
{
    private const int Bsize = Biobuf.Bsize;
    private readonly List<string> errors = [];
    private Process init = null!;

    public async Task InitializeAsync() => init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // A write that fails is told to Berror once: the bytes it was writing are not counted, and the
    // buffer takes nothing more, nor writes again when flushed.
    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AFailedWriteIsToldOnceAndEndsTheBuffersWriting()
    {
        Biobuf bp = Buffer(9);
        Assert.Equal(Bsize, await bp.BwriteAsync(new byte[Bsize]));
        Assert.Equal(Biobuf.Beof, await bp.BwriteAsync(new byte[1]));
        Assert.Equal(["write error: fd out of range or not open"], errors);
        Assert.Equal(Biobuf.Beof, await bp.BwriteAsync(new byte[1]));
        Assert.Equal(Biobuf.Beof, await bp.BflushAsync());
        Assert.Single(errors);
        Assert.Equal("fd out of range or not open", Errstr.Get(init));
    }

    // A flush that fails is told to Berror, and the buffer, as good as full, takes no more.
    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AFailedFlushIsToldAndEndsTheBuffersWriting()
    {
        Biobuf bp = Buffer(9);
        Assert.Equal(0, await bp.BflushAsync());
        Assert.Equal(1, await bp.BwriteAsync(new byte[1]));
        Assert.Equal(Biobuf.Beof, await bp.BflushAsync());
        Assert.Equal(["write error: fd out of range or not open"], errors);
        Assert.Equal(Biobuf.Beof, await bp.BwriteAsync(new byte[1]));
        Assert.Equal(Biobuf.Beof, await bp.BflushAsync());
        Assert.Single(errors);
    }

    // A write an interrupt fails is not an error: the buffer goes on, holding what it held before
    // the call whose write failed. The pipe takes a write and then holds its writer until no more
    // than its limit is queued, so the second whole buffer it is given waits, and is interrupted.
    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnInterruptedWriteLeavesTheBufferWriting()
    {
        init.Notify(note => note == "interrupt");
        var (r, w) = await init.PipeAsync();
        Stat nul = Process.NullDir;
        await init.FWStatAsync(w, new Stat(0, nul.Type, nul.Dev, nul.Qid, nul.Mode, nul.Atime, nul.Mtime, Bsize, nul.Name, nul.Uid, nul.Gid, nul.Muid));
        Biobuf bp = Buffer(w);
        Assert.Equal(Bsize, await bp.BwriteAsync(new byte[Bsize]));
        Assert.Equal(1, await bp.BwriteAsync(new byte[] { 1 }));
        Task<long> blocked = bp.BwriteAsync(new byte[Bsize]);
        while (!blocked.IsCompleted)
        {
            init.PostNote("interrupt");
            await Task.WhenAny(blocked, Task.Delay(10));
        }

        Assert.Equal(Biobuf.Beof, await blocked);
        Assert.Equal("interrupted", Errstr.Get(init));
        Assert.Empty(errors);
        for (int drained = 0; drained < 2 * Bsize;)
        {
            drained += (await init.ReadAsync(r, Bsize)).Length;
        }

        Assert.Equal(1, await bp.BwriteAsync(new byte[] { 2 }));
        Assert.Equal(0, await bp.BflushAsync());
        Assert.Equal(new byte[] { 1, 2 }, (await init.ReadAsync(r, Bsize)).ToArray());
        Assert.Empty(errors);
    }

    // Bread reads until it has what it was asked for, running Iof before each read; at the end of
    // input it gives what it has and reads no more, and a read that fails is told to Berror and
    // gives Beof from then on.
    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task BreadReadsUntilItHasEnoughOrTheInputEnds()
    {
        var (r, w) = await init.PipeAsync();
        int iof = 0;
        Biobuf bp = Buffer(r);
        bp.Iof = () => Task.FromResult(iof++);
        await init.WriteAsync(w, "abc"u8.ToArray());
        byte[] p = new byte[5];
        Task<int> reading = bp.BreadAsync(p, 5);
        while ((await init.PipeReadinessAsync(r))!.Value.Queued != 0)
        {
            await Task.Delay(1);
        }

        await init.WriteAsync(w, "de"u8.ToArray());
        Assert.Equal(5, await reading);
        Assert.Equal("abcde"u8.ToArray(), p);
        await init.WriteAsync(w, "f"u8.ToArray());
        await init.CloseAsync(w);
        Assert.Equal(1, await bp.BreadAsync(p, 5));
        Assert.Equal(0, await bp.BreadAsync(p, 5));
        Assert.Equal(4, iof);

        int fd = await init.CreateAsync("/tmp/b", NinePConstants.ORDWR, 0b110_110_110);
        Biobuf file = Buffer(fd);
        Assert.Equal(0, await file.BreadAsync(p, 5));
        await init.WriteAsync(await init.OpenAsync("/tmp/b", NinePConstants.OWRITE), "late"u8.ToArray());
        Assert.Equal(0, await file.BreadAsync(p, 5));
        Assert.Equal("late"u8.ToArray(), (await init.ReadAsync(fd, 5)).ToArray());

        Biobuf closed = Buffer(9);
        Assert.Equal(Biobuf.Beof, await closed.BreadAsync(p, 5));
        Assert.Equal(["read error: fd out of range or not open"], errors);
        Assert.Equal(Biobuf.Beof, await closed.BreadAsync(p, 5));
        Assert.Single(errors);
    }

    private Biobuf Buffer(int fd) => new(init, fd)
    {
        Errorf = message =>
        {
            errors.Add(message);
            return Task.CompletedTask;
        },
    };
}
