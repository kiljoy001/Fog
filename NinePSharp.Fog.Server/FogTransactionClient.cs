using System.Text;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Server;

/// <summary>One bounded control transaction over ordinary 9P files. Retry preserves the known server ID.</summary>
public sealed class FogTransactionClient
{
    private static readonly FogRecordSchema StatusSchema = new("fogtx-v1", ["id", "state", "error"], ["id", "state"], ["id"]);
    private readonly Func<CancellationToken, Task<Stream>> connect;
    private readonly string node;
    private readonly uint messageSize;
    private readonly int maximumBytes;
    private readonly TimeSpan deadline;

    public FogTransactionClient(Func<CancellationToken, Task<Stream>> connect, string node, uint messageSize, int maximumBytes, TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(connect);
        if (messageSize < 256 || messageSize > int.MaxValue || maximumBytes <= 0 || deadline <= TimeSpan.Zero || string.IsNullOrEmpty(node))
            throw new ArgumentException("Invalid transaction client limits.");
        this.connect = connect;
        this.node = node;
        this.messageSize = messageSize;
        this.maximumBytes = maximumBytes;
        this.deadline = deadline;
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> ExecuteAsync(string service, IReadOnlyDictionary<string, byte[]> inputs,
        IReadOnlyList<string> outputs, CancellationToken cancellationToken = default)
    {
        if (!inputs.ContainsKey("request") || inputs.Values.Sum(bytes => (long)bytes.Length) > maximumBytes || outputs.Count == 0 ||
            outputs.Distinct(StringComparer.Ordinal).Count() != outputs.Count || !SafeName(service) ||
            inputs.Keys.Any(name => !SafeName(name)) || outputs.Any(name => !SafeName(name)))
            throw new ArgumentException("Invalid bounded transaction request.");
        // Freeze caller-owned input across reconnects. A retry never changes the request under its ID.
        var frozen = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        string? id = null;
        var retries = new Queue<byte>([0, 0]);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                await using var client = new NinePSequentialClient(await connect(timeout.Token).ConfigureAwait(false), messageSize);
                await client.NegotiateAsync(timeout.Token).ConfigureAwait(false);
                await client.ExchangeAsync<Rattach>(tag => new Tattach(tag, 1, NinePConstants.NoFid, node, "runtime"), timeout.Token).ConfigureAwait(false);
                if (id is null)
                {
                    byte[] bytes = await ReadFile(client, ["control", service, "clone"], 2, 86, timeout.Token).ConfigureAwait(false);
                    string allocated = Encoding.ASCII.GetString(bytes);
                    if (!ValidTransactionId(allocated)) throw new IOException("Invalid transaction identity.");
                    id = allocated[..^1];
                }

                return await CompleteTransactionAsync(client, service, id, frozen, outputs, timeout.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                if (!retries.TryDequeue(out _)) throw;
                // New TLS/9P session, same transaction ID. Unknown clone IDs have no uploaded effect.
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, byte[]>> CompleteTransactionAsync(NinePSequentialClient client, string service,
        string id, IReadOnlyDictionary<string, byte[]> inputs, IReadOnlyList<string> outputs, CancellationToken cancellation)
    {
        const uint temporaryFid = 2;
        byte[] statusBytes = await ReadFile(client, ["control", service, id, "status"], temporaryFid, 1024, cancellation).ConfigureAwait(false);
        var statusRows = StatusSchema.Parse(statusBytes, 1024, 1);
        if (statusRows.Count != 1) throw new IOException("Invalid transaction status.");
        var status = statusRows[0];
        if (status["id"] != id) throw new IOException("Mismatched transaction identity.");
        if (status["state"] == "staging")
        {
            foreach (var file in inputs)
                await WriteFile(client, ["control", service, id, file.Key], temporaryFid, file.Value, cancellation).ConfigureAwait(false);
        }
        else if (status["state"] is not ("done" or "committing")) throw new IOException("Invalid transaction state.");

        await WriteFile(client, ["control", service, id, "ctl"], temporaryFid, "commit\n"u8.ToArray(), cancellation).ConfigureAwait(false);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int remaining = maximumBytes;
        foreach (string output in outputs)
        {
            byte[] bytes = await ReadFile(client, ["control", service, id, output], temporaryFid, remaining, cancellation).ConfigureAwait(false);
            result.Add(output, bytes);
            remaining -= bytes.Length;
        }
        await ReleaseAsync(client, service, id, temporaryFid, cancellation).ConfigureAwait(false);
        return result;
    }

    private static async Task ReleaseAsync(NinePSequentialClient client, string service, string id, uint fid, CancellationToken cancellation)
    {
        // All outputs are locally frozen now. A lost release reply must never retry the operation.
        try
        {
            await WriteFile(client, ["control", service, id, "ctl"], fid, "release\n"u8.ToArray(), cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or NinePException or OperationCanceledException)
        {
            // The bounded server retention is the fallback cleanup, not a new transaction.
        }
    }

    private static async Task<byte[]> ReadFile(NinePSequentialClient client, string[] path, uint fid, int maximum, CancellationToken cancellation)
    {
        await Walk(client, path, fid, cancellation).ConfigureAwait(false);
        await client.ExchangeAsync<Ropen>(tag => new Topen(tag, fid, NinePConstants.OREAD), cancellation).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        while (bytes.Length < maximum)
        {
            uint requested = (uint)Math.Min(client.MessageSize - 11, maximum - bytes.Length);
            var read = await client.ExchangeAsync<Rread>(tag => new Tread(tag, fid, (ulong)bytes.Length, requested), cancellation).ConfigureAwait(false);
            if (read.Count > requested) throw new IOException("Oversized control file.");
            if (read.Count == 0)
            {
                await client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, fid), cancellation).ConfigureAwait(false);
                return bytes.ToArray();
            }
            bytes.Write(read.Data.Span);
        }

        var probe = await client.ExchangeAsync<Rread>(tag => new Tread(tag, fid, (ulong)bytes.Length, 1), cancellation).ConfigureAwait(false);
        if (probe.Count != 0) throw new IOException("Oversized control file.");
        await client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, fid), cancellation).ConfigureAwait(false);
        return bytes.ToArray();
    }

    private static async Task WriteFile(NinePSequentialClient client, string[] path, uint fid, byte[] bytes, CancellationToken cancellation)
    {
        await Walk(client, path, fid, cancellation).ConfigureAwait(false);
        await client.ExchangeAsync<Ropen>(tag => new Topen(tag, fid, NinePConstants.OWRITE), cancellation).ConfigureAwait(false);
        int offset = 0;
        while (offset < bytes.Length)
        {
            byte[] fragment = bytes.AsSpan(offset, Math.Min(bytes.Length - offset, (int)client.MessageSize - 24)).ToArray();
            var write = await client.ExchangeAsync<Rwrite>(tag => new Twrite(tag, fid, (ulong)offset, fragment), cancellation).ConfigureAwait(false);
            if (write.Count != fragment.Length) throw new IOException("Ambiguous partial control write.");
            offset += fragment.Length;
        }
        await client.ExchangeAsync<Rclunk>(tag => new Tclunk(tag, fid), cancellation).ConfigureAwait(false);
    }

    private static async Task Walk(NinePSequentialClient client, string[] path, uint fid, CancellationToken cancellation)
    {
        var walk = await client.ExchangeAsync<Rwalk>(tag => new Twalk(tag, 1, fid, path), cancellation).ConfigureAwait(false);
        if (walk.Wqid.Length != path.Length) throw new NinePException("tx-expired");
    }

    private static bool ValidTransactionId(string text)
    {
        if (text.Length is < 67 or > 86 || text[^1] != '\n' || text[64] != '-' ||
            text[..64].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) return false;
        string count = text[65..^1];
        return ulong.TryParse(count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong value) &&
            value > 0 && value.ToString(System.Globalization.CultureInfo.InvariantCulture) == count;
    }

    private static bool SafeName(string value) => value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
