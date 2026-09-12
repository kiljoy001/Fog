using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Tests.Steps;

[Binding]
public sealed class FogControlSteps : IDisposable
{
    private readonly FogTransactionStore store = new(FogTransactionTests.Limits with { MaxInputBytes = 1024, MaxSnapshotBytes = 1024, MaxReservedBytes = 8192 }, _ => true);
    private readonly byte[] request = "schema=fixture-v1\n\tcol=id\n\tcol=value\n\nid=one\n\tvalue=hello\n\n"u8.ToArray();
    private readonly List<uint> acknowledgements = [];
    private string id = string.Empty;
    private FogUpload? upload;
    private FogException? error;
    private int effects;

    [Given("a core transaction containing a canonical sealed request")]
    public void SealedRequest() => Stage(request, seal: true);

    [Given("a core transaction containing duplicate physical request rows")]
    public void DuplicateRequest() => Stage(request.Concat("id=one\n\tvalue=hello\n\n"u8.ToArray()).ToArray(), seal: true);

    [Given("a core transaction containing a canonical unsealed request")]
    public void UnsealedRequest() => Stage(request, seal: false);

    [When("its owner writes the commit command twice")]
    public async Task CommitTwice()
    {
        await Commit();
        await Commit();
    }

    [When("its owner writes the commit command")]
    public async Task Commit()
    {
        var ctl = new FogControlFile(store, inputs =>
        {
            _ = FogRecordTests.Schema.Parse(inputs["request"].Span, 1024, 4);
            return FogTransactionTests.Plan(inputs["request"].ToArray(), () => effects++);
        });
        try
        {
            acknowledgements.Add(await ctl.WriteAsync("alice", id, "commit\n"u8.ToArray()));
        }
        catch (FogException exception)
        {
            error = exception;
        }
    }

    [Then("the core acknowledges seven bytes each time")]
    public void Acknowledged()
    {
        Assert.Null(error);
        Assert.Equal(new uint[] { 7, 7 }, acknowledgements);
    }

    [Then("the fixture effect executes exactly once")]
    public void OneEffect() => Assert.Equal(1, effects);

    [Then("the retained reply is the original request bytes")]
    public void OriginalReply() => Assert.Equal(request, store.ReadOutput("alice", id, "reply", 0, uint.MaxValue));

    [Then("the core rejects (.*) and remains staging")]
    public void Rejected(string code)
    {
        Assert.Equal(code, Assert.IsType<FogException>(error).Code);
        Assert.Empty(acknowledgements);
        Assert.Equal("staging", store.Status("alice", id).State);
    }

    [Then("the fixture effect has not executed")]
    public void NoEffect() => Assert.Equal(0, effects);

    public void Dispose() => upload?.Dispose();

    private void Stage(byte[] bytes, bool seal)
    {
        id = store.Clone("alice");
        upload = store.OpenInput("alice", id, "s", "request");
        upload.Write(0, bytes.AsSpan(0, 3));
        upload.Write(3, bytes.AsSpan(3));
        Assert.Equal(0, effects);
        if (seal)
        {
            upload.Seal();
        }
    }
}
