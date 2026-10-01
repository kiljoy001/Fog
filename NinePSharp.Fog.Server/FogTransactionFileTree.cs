using System.Text;
using System.Collections.Frozen;
using NinePSharp.Constants;

namespace NinePSharp.Fog.Server;

/// <summary>A host-local registration. Its commit callback must validate operation-specific required inputs.</summary>
public sealed record FogTransactionService(
    string Name,
    FogTransactionStore Store,
    IReadOnlySet<string> InputFiles,
    IReadOnlySet<string> OutputFiles,
    Func<FogPrincipal, string, CancellationToken, Task> Commit);

/// <summary>The real /control transaction tree. Only opening clone allocates a transaction.</summary>
public sealed class FogTransactionFileTree : FogFileTree
{
    private static readonly FogRecordSchema StatusSchema = new("fogtx-v1", ["id", "state", "error"], ["id", "state"], ["id"]);
    private readonly object gate = new();
    private readonly Dictionary<string, ServiceNodes> services;
    private readonly FogFileNode root;
    private readonly FogFileNode control;

    public FogTransactionFileTree(IEnumerable<FogTransactionService> registrations)
    {
        root = new(AllocateQid(), "/", true);
        control = new(AllocateQid(), "control", true);
        services = new(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var service = registration with
            {
                InputFiles = registration.InputFiles.ToFrozenSet(StringComparer.Ordinal),
                OutputFiles = registration.OutputFiles.ToFrozenSet(StringComparer.Ordinal),
            };
            if (!ValidName(service.Name) || !service.InputFiles.Contains("request") || !service.OutputFiles.Contains("reply") ||
                service.InputFiles.Concat(service.OutputFiles).Any(name => !ValidName(name) || name is "ctl" or "status" or "clone") ||
                service.InputFiles.Overlaps(service.OutputFiles) || !services.TryAdd(service.Name,
                    new ServiceNodes(service, new(AllocateQid(), service.Name, true, service.Name), new(AllocateQid(), "clone", false, service.Name, File: "clone"))))
                throw new ArgumentException("Invalid transaction service registration.");
        }
    }

    public override FogFileNode Root => root;

    public override FogFileNode Walk(FogPrincipal principal, FogFileNode directory, string name)
    {
        lock (gate)
        {
            Check(principal, directory);
            if (!directory.Directory) throw new FogException("invalid-request");
            if (name == ".") return directory;
            if (name == "..") return Parent(directory);
            if (directory == root && name == "control") return control;
            if (directory == control && services.TryGetValue(name, out var found)) return found.Root;
            if (directory.Service.Length != 0)
            {
                var service = services[directory.Service];
                if (directory.Transaction.Length == 0)
                {
                    if (name == "clone") return service.CloneFile;
                    if (service.Transactions.TryGetValue(name, out var transaction))
                    {
                        _ = service.Service.Store.Status(principal.Owner, name);
                        return transaction.Root;
                    }
                }
                else if (service.Transactions[directory.Transaction].Files.TryGetValue(name, out var file)) return file;
            }

            throw new FogException("tx-expired");
        }
    }

    public override IReadOnlyList<FogFileNode> List(FogPrincipal principal, FogFileNode directory)
    {
        lock (gate)
        {
            Check(principal, directory);
            if (directory == root) return [control];
            if (directory == control) return services.Values.Select(service => service.Root).ToArray();
            var service = services[directory.Service];
            // Knowing a transaction ID is sufficient for walking, never for authorization; don't enumerate IDs.
            return directory.Transaction.Length == 0 ? [service.CloneFile] : service.Transactions[directory.Transaction].Files.Values.ToArray();
        }
    }

    public override void Check(FogPrincipal principal, FogFileNode node)
    {
        if (node.Transaction.Length != 0) _ = services[node.Service].Service.Store.Status(principal.Owner, node.Transaction);
    }

    public override FogOpenFile Open(FogPrincipal principal, string session, FogFileNode node, byte mode, long snapshotBudget)
    {
        lock (gate)
        {
            var service = services[node.Service];
            if (node.File == "clone")
            {
                RequireMode(mode, NinePConstants.OREAD);
                if (snapshotBudget < 86) throw new FogException("snapshot-limit");
                Prune(service);
                string id = service.Service.Store.Clone(principal.Owner);
                var directory = new FogFileNode(AllocateQid(), id, true, node.Service, id);
                var files = service.Service.InputFiles.Concat(service.Service.OutputFiles).Concat(["ctl", "status"])
                    .ToDictionary(name => name, name => new FogFileNode(AllocateQid(), name, false, node.Service, id, name), StringComparer.Ordinal);
                service.Transactions.Add(id, new TransactionNodes(directory, files));

                return new FogOpenFile(Encoding.ASCII.GetBytes(id + "\n"));
            }

            if (node.File == "ctl")
            {
                RequireMode(mode, NinePConstants.OWRITE);
                return new FogOpenFile(write: async (_, bytes, cancellation) =>
                {
                    if (bytes.Span.SequenceEqual("commit\n"u8))
                    {
                        await service.Service.Commit(principal, node.Transaction, cancellation);
                        return 7;
                    }
                    if (bytes.Span.SequenceEqual("release\n"u8))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        service.Service.Store.Release(principal.Owner, node.Transaction);
                        return 8;
                    }
                    throw new FogException("invalid-request");
                });
            }

            if (service.Service.InputFiles.Contains(node.File))
            {
                RequireMode(mode, NinePConstants.OWRITE);
                var upload = service.Service.Store.OpenInput(principal.Owner, node.Transaction, session, node.File);
                return new FogOpenFile(write: (offset, bytes, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    upload.Write(offset, bytes.Span);
                    return Task.FromResult((uint)bytes.Length);
                }, close: seal => { if (seal) upload.Seal(); else upload.Dispose(); });
            }

            RequireMode(mode, NinePConstants.OREAD);
            if (node.File == "status")
            {
                if (snapshotBudget < 1024) throw new FogException("snapshot-limit");
                var status = service.Service.Store.Status(principal.Owner, node.Transaction);
                return new FogOpenFile(StatusSchema.Serialize([new Dictionary<string, string?>
                {
                    ["id"] = status.Id, ["state"] = status.State, ["error"] = status.Error,
                }], 1024, 1));
            }
            if (service.Service.Store.OutputSize(principal.Owner, node.Transaction, node.File) > snapshotBudget) throw new FogException("snapshot-limit");
            return new FogOpenFile(service.Service.Store.SnapshotOutput(principal.Owner, node.Transaction, node.File));
        }
    }

    public override void CloseSession(string session)
    {
        foreach (var service in services.Values) service.Service.Store.CloseSession(session);
    }

    private FogFileNode Parent(FogFileNode node) => node == root || node == control ? root :
        node.Transaction.Length == 0 ? control : services[node.Service].Root;

    private static void Prune(ServiceNodes service)
    {
        var live = service.Service.Store.LiveIds();
        foreach (string id in service.Transactions.Keys.Where(id => !live.Contains(id)).ToArray()) service.Transactions.Remove(id);
    }

    private static void RequireMode(byte mode, byte expected)
    {
        if (mode != expected) throw new FogException("denied");
    }

    private static bool ValidName(string name) => name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private sealed record TransactionNodes(FogFileNode Root, Dictionary<string, FogFileNode> Files);
    private sealed record ServiceNodes(FogTransactionService Service, FogFileNode Root, FogFileNode CloneFile)
    {
        internal readonly Dictionary<string, TransactionNodes> Transactions = new(StringComparer.Ordinal);
    }
}
