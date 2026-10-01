using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class TransactionTreeTests
{
    [Fact]
    public async Task CanceledWritesSessionCloseAndPruningHaveObservableLifecycleEffects()
    {
        using var fixture = new ControlFixture();
        var principal = fixture.Policy.Attach("worker", fixture.NodeCertificate);
        var tree = fixture.Tree;
        FogFileNode control = tree.Walk(principal, tree.Root, "control");
        FogFileNode service = tree.Walk(principal, control, "fixture");
        Reject("tx-expired", () => tree.Walk(principal, tree.Root, "missing"));
        using FogOpenFile clone = tree.Open(principal, "clone-session", tree.Walk(principal, service, "clone"), NinePConstants.OREAD, 86);
        string id = Encoding.ASCII.GetString(clone.Snapshot!).TrimEnd('\n');
        FogFileNode transaction = tree.Walk(principal, service, id);
        FogFileNode request = tree.Walk(principal, transaction, "request");
        FogFileNode ctl = tree.Walk(principal, transaction, "ctl");
        using FogOpenFile upload = tree.Open(principal, "upload-session", request, NinePConstants.OWRITE, 1024);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload.Write!(0, new byte[] { 1 }, canceled.Token));
        using FogOpenFile controlFile = tree.Open(principal, "ctl-session", ctl, NinePConstants.OWRITE, 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controlFile.Write!(0, "release\n"u8.ToArray(), canceled.Token));
        Assert.Equal("staging", fixture.Store.Status(principal.Owner, id).State);

        tree.CloseSession("upload-session");
        Reject("upload-open", () => upload.Write!(0, new byte[] { 1 }, CancellationToken.None).GetAwaiter().GetResult());
        fixture.Store.Release(principal.Owner, id);
        using FogOpenFile replacement = tree.Open(principal, "replacement", tree.Walk(principal, service, "clone"), NinePConstants.OREAD, 86);

        var registrations = (System.Collections.IDictionary)typeof(FogTransactionFileTree)
            .GetField("services", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(tree)!;
        object serviceNodes = registrations["fixture"]!;
        var transactions = (System.Collections.IDictionary)serviceNodes.GetType()
            .GetField("Transactions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(serviceNodes)!;
        Assert.Single(transactions);

        using FogOpenFile abandoned = tree.Open(principal, "abandoned", tree.Walk(principal,
            tree.Walk(principal, service, Encoding.ASCII.GetString(replacement.Snapshot!).TrimEnd('\n')), "request"), NinePConstants.OWRITE, 1024);
        abandoned.Dispose();
        using FogOpenFile reopened = tree.Open(principal, "reopened", tree.Walk(principal,
            tree.Walk(principal, service, Encoding.ASCII.GetString(replacement.Snapshot!).TrimEnd('\n')), "request"), NinePConstants.OWRITE, 1024);
    }

    [Fact]
    public void RegistrationOwnsItsFileSetsAndRejectsAmbiguousNames()
    {
        using var fixture = new ControlFixture();
        var inputs = new HashSet<string> { "request", "Payload_09-z" };
        var outputs = new HashSet<string> { "reply" };
        var registration = Service(fixture) with { Name = new string('x', 64), InputFiles = inputs, OutputFiles = outputs };
        var tree = new FogTransactionFileTree([registration]);
        Assert.Equal("/", tree.Root.Name);
        Assert.True(tree.Root.QidPath > 0);
        inputs.Clear();
        outputs.Add("unexpected");
        var principal = fixture.Policy.Attach("worker", fixture.NodeCertificate);
        var service = tree.Walk(principal, tree.Walk(principal, tree.Root, "control"), registration.Name);
        using var clone = tree.Open(principal, "s", tree.Walk(principal, service, "clone"), NinePConstants.OREAD, 86);
        string id = Encoding.ASCII.GetString(clone.Snapshot!).TrimEnd('\n');
        var transaction = tree.Walk(principal, service, id);
        Assert.Equal(new[] { "Payload_09-z", "ctl", "reply", "request", "status" }, tree.List(principal, transaction).Select(node => node.Name).Order(StringComparer.Ordinal).ToArray());
        foreach (var invalid in new[]
        {
            Service(fixture) with { Name = "" }, Service(fixture) with { Name = new string('x', 65) },
            Service(fixture) with { Name = "bad/name" }, Service(fixture) with { Name = "é" },
            Service(fixture) with { InputFiles = new HashSet<string>() },
            Service(fixture) with { OutputFiles = new HashSet<string>() },
            Service(fixture) with { InputFiles = new HashSet<string> { "request", "reply" } },
        }) Invalid(() => new FogTransactionFileTree([invalid]));
        foreach (string name in new[] { "", "ctl", "status", "clone", ".", "..", "a/b", "é", new string('x', 65) })
        {
            Invalid(() => new FogTransactionFileTree([Service(fixture) with { InputFiles = new HashSet<string> { "request", name } }]));
            Invalid(() => new FogTransactionFileTree([Service(fixture) with { OutputFiles = new HashSet<string> { "reply", name } }]));
        }
        Invalid(() => new FogTransactionFileTree([Service(fixture), Service(fixture)]));
    }

    [Fact]
    public async Task StatusSnapshotsRemainStableAndOutputBudgetsAreCheckedBeforeCopying()
    {
        using var fixture = new ControlFixture();
        await fixture.Initialize();
        string id = await fixture.Clone();
        var principal = fixture.Policy.Attach("worker", fixture.NodeCertificate);
        var tree = fixture.Tree;
        var control = tree.Walk(principal, tree.Root, "control");
        var service = tree.Walk(principal, control, "fixture");
        Reject("denied", () => tree.Open(principal, "s", tree.Walk(principal, service, "clone"), NinePConstants.OWRITE, 86));
        var transaction = tree.Walk(principal, service, id);
        var status = tree.Walk(principal, transaction, "status");
        var reply = tree.Walk(principal, transaction, "reply");
        Reject("snapshot-limit", () => tree.Open(principal, "s", status, NinePConstants.OREAD, 1023));
        Reject("denied", () => tree.Open(principal, "s", status, NinePConstants.OWRITE, 1024));
        Reject("not-ready", () => tree.Open(principal, "s", reply, NinePConstants.OREAD, 4096));
        using var before = tree.Open(principal, "s", status, NinePConstants.OREAD, 1024);
        byte[] staging = before.Snapshot!.ToArray();
        await fixture.Upload(id, [1, 2, 3]);
        await fixture.Write(4, "commit\n"u8.ToArray());
        using var after = tree.Open(principal, "s", status, NinePConstants.OREAD, 1024);
        var schema = new FogRecordSchema("fogtx-v1", ["id", "state", "error"], ["id", "state"], ["id"]);
        Assert.Equal("staging", schema.Parse(before.Snapshot!, 1024, 1).Single()["state"]);
        Assert.Equal("done", schema.Parse(after.Snapshot!, 1024, 1).Single()["state"]);
        Assert.Equal(staging, before.Snapshot);
        Reject("snapshot-limit", () => tree.Open(principal, "s", reply, NinePConstants.OREAD, 2));
        using var output = tree.Open(principal, "s", reply, NinePConstants.OREAD, 3);
        Assert.Equal(new byte[] { 1, 2, 3 }, output.Snapshot);
        Reject("denied", () => tree.Open(principal, "s", reply, NinePConstants.OWRITE, 3));
        Reject("invalid-request", () => tree.Walk(principal, reply, "."));
        Assert.Equal(tree.Root, tree.Walk(principal, tree.Root, ".."));
        Assert.Equal(tree.Root, tree.Walk(principal, control, ".."));
        Assert.Equal(control, tree.Walk(principal, service, ".."));
        Assert.Equal(service, tree.Walk(principal, transaction, ".."));
        Assert.Equal(transaction, tree.Walk(principal, transaction, "."));
        Assert.Equal(new[] { "clone" }, tree.List(principal, service).Select(node => node.Name));
        var other = fixture.Policy.Attach("other", fixture.OtherCertificate);
        Reject("denied", () => tree.Walk(other, service, id));
        Reject("denied", () => tree.Walk(other, transaction, "reply"));
        Reject("denied", () => tree.List(other, transaction));
        fixture.Store.Release(principal.Owner, id);
        Reject("tx-expired", () => tree.Walk(principal, service, id));
        Reject("tx-expired", () => tree.Open(principal, "s", reply, NinePConstants.OREAD, 3));
        string replacement = await fixture.Clone();
        Assert.NotEqual(id, replacement);
        Assert.NotEqual(transaction.QidPath, tree.Walk(principal, service, replacement).QidPath);
    }

    private static FogTransactionService Service(ControlFixture fixture) => new("fixture", fixture.Store,
        new HashSet<string> { "request" }, new HashSet<string> { "reply" }, (_, _, _) => Task.CompletedTask);

    private static void Invalid(Action action) =>
        Assert.Equal("Invalid transaction service registration.", Assert.Throws<ArgumentException>(action).Message);

    private static void Reject(string code, Action action) => Assert.Equal(code, Assert.Throws<FogException>(action).Code);
}
