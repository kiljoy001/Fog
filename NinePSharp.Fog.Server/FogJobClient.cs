using System.Text;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Server;

/// <summary>Explicit job IDs let callers reconnect without resubmitting a completed computation.</summary>
public sealed class FogJobClient : IAsyncDisposable
{
    private readonly NinePSequentialClient wire;
    private uint fid = 2;

    private FogJobClient(Stream stream) => wire = new(stream, 8192);

    public static async Task<FogJobClient> ConnectAsync(Stream stream, string node, CancellationToken cancellation = default)
    {
        var client = new FogJobClient(stream);
        try
        {
            await client.wire.NegotiateAsync(cancellation).ConfigureAwait(false);
            await client.wire.ExchangeAsync<Rattach>(tag => new Tattach(tag, 1, NinePConstants.NoFid, node, "runtime"), cancellation).ConfigureAwait(false);
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<string> CloneAsync(CancellationToken cancellation = default)
    {
        string value = Encoding.ASCII.GetString(await Read(["compute", "clone"], 86, cancellation).ConfigureAwait(false));
        if (!value.EndsWith('\n')) throw new IOException("Invalid job identity.");
        string id = value[..^1];
        ValidateId(id);
        return id;
    }

    public async Task UploadAsync(string id, FogMathJob spec, byte[] source, CancellationToken cancellation = default)
    {
        ValidateId(id);
        if (source.Length > 1048576) throw new ArgumentException("Expression is too large.");
        source = source.ToArray();
        await Write(["compute", id, "spec"], spec.Serialize(id), cancellation).ConfigureAwait(false);
        await Write(["compute", id, "input"], source, cancellation).ConfigureAwait(false);
    }

    public Task ControlAsync(string id, string command, CancellationToken cancellation = default)
    {
        ValidateId(id);
        if (command is not ("start" or "cancel" or "release")) throw new ArgumentException("Invalid job command.");
        return Write(["compute", id, "ctl"], Encoding.ASCII.GetBytes(command + "\n"), cancellation);
    }

    public async Task<IReadOnlyDictionary<string, string?>> StatusAsync(string id, CancellationToken cancellation = default)
    {
        ValidateId(id);
        var rows = FogMathJob.StatusSchema.Parse(await Read(["compute", id, "status"], 2048, cancellation).ConfigureAwait(false), 2048, 1);
        if (rows.Count != 1 || rows[0]["job"] != id) throw new IOException("Invalid job status.");
        return rows[0];
    }

    public Task<byte[]> ResultAsync(string id, CancellationToken cancellation = default)
    {
        ValidateId(id);
        return Read(["compute", id, "result"], 65536, cancellation);
    }

    public async Task<byte[]> WaitAsync(string id, CancellationToken cancellation)
    {
        while (true)
        {
            var status = await StatusAsync(id, cancellation).ConfigureAwait(false);
            switch (status["state"])
            {
                case "succeeded": return await ResultAsync(id, cancellation).ConfigureAwait(false);
                case "failed" or "cancelled": throw new FogException(status["error"] ?? "worker-lost");
                case "queued" or "running": await Task.Delay(100, cancellation).ConfigureAwait(false); break;
                default: throw new FogException("not-ready");
            }
        }
    }

    private async Task<uint> Open(string[] path, byte mode, CancellationToken cancellation)
    {
        if (fid == uint.MaxValue) throw new IOException("Session fid allowance exhausted.");
        uint opened = fid++;
        var walked = await wire.ExchangeAsync<Rwalk>(tag => new Twalk(tag, 1, opened, path), cancellation).ConfigureAwait(false);
        if (walked.Wqid.Length != path.Length) throw new IOException("Job path does not exist.");
        await wire.ExchangeAsync<Ropen>(tag => new Topen(tag, opened, mode), cancellation).ConfigureAwait(false);
        return opened;
    }

    private async Task<byte[]> Read(string[] path, int maximum, CancellationToken cancellation)
    {
        uint opened = await Open(path, NinePConstants.OREAD, cancellation).ConfigureAwait(false);
        using var result = new MemoryStream();
        while (true)
        {
            uint count = (uint)System.Math.Min(wire.MessageSize - 11, maximum - result.Length + 1);
            var reply = await wire.ExchangeAsync<Rread>(tag => new Tread(tag, opened, (ulong)result.Length, count), cancellation).ConfigureAwait(false);
            if (reply.Count > count || reply.Count > maximum - result.Length) throw new IOException("Oversized job file.");
            if (reply.Count == 0) break;
            result.Write(reply.Data.Span);
        }
        await wire.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, opened), cancellation).ConfigureAwait(false);
        return result.ToArray();
    }

    private async Task Write(string[] path, byte[] bytes, CancellationToken cancellation)
    {
        uint opened = await Open(path, NinePConstants.OWRITE, cancellation).ConfigureAwait(false);
        for (int offset = 0; offset < bytes.Length;)
        {
            byte[] chunk = bytes.AsSpan(offset, System.Math.Min(bytes.Length - offset, (int)wire.MessageSize - 24)).ToArray();
            var reply = await wire.ExchangeAsync<Rwrite>(tag => new Twrite(tag, opened, (ulong)offset, chunk), cancellation).ConfigureAwait(false);
            if (reply.Count != chunk.Length) throw new IOException("Ambiguous partial job upload.");
            offset += chunk.Length;
        }
        await wire.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, opened), cancellation).ConfigureAwait(false);
    }

    private static void ValidateId(string id)
    {
        if (id.Length is < 66 or > 85 || id[64] != '-' || id[..64].Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            id[65..].Any(c => !char.IsAsciiDigit(c))) throw new ArgumentException("Invalid job identity.");
    }

    public ValueTask DisposeAsync() => wire.DisposeAsync();
}
