using System.Buffers.Binary;
using NinePSharp.Fog.Gefs.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class ConnSteps(StoreContext context)
{
    private readonly List<ScriptedAuth> conversations = [];
    private NinePClient? client;
    private string? factotumDown;
    private byte[] reply = [];

    private NinePClient Client => client!;

    [Given(@"^factotum cannot be reached, failing with ""(.*)""$")]
    public void GivenFactotumDown(string error) => factotumDown = error;

    [Given("a client connected to the server")]
    public void GivenClient()
        => client = new NinePClient(context.Server!, () =>
        {
            if (factotumDown is { } error)
            {
                throw new IOException(error);
            }

            var auth = new ScriptedAuth();
            conversations.Add(auth);
            return Task.FromResult<IGefsAuth>(auth);
        });

    [Given(@"^the client has versioned the connection with a message size of (\d+)$")]
    public async Task GivenVersioned(int msize) => Assert.Equal($"Rversion {msize} 9P2000", NinePClient.Text(await Client.RpcAsync($"Tversion {msize} 9P2000")));

    // Tauth, the proof written, Tattach, and the auth file clunked, as mount's amount does.
    [Given(@"^the client has attached fid (\d+) as (\w+) to ""(.*)""$")]
    public async Task GivenAttached(uint fid, string user, string aname)
    {
        uint afid = 1000 + fid;
        string[] said =
        [
            NinePClient.Text(await Client.RpcAsync($"Tauth {afid} {user} {aname}")),
            NinePClient.Text(await Client.RpcAsync($"Twrite {afid} 0 {user}")),
            NinePClient.Text(await Client.RpcAsync($"Tattach {fid} {afid} {user} {aname}")),
            NinePClient.Text(await Client.RpcAsync($"Tclunk {afid}")),
        ];
        Assert.Equal(["Rauth auth", $"Rwrite {user.Length}", "Rattach dir", "Rclunk"], said);
    }

    [When("the client sends:")]
    [Then("the client sends:")]
    public async Task WhenSends(Table table)
    {
        foreach (DataTableRow row in table.Rows)
        {
            reply = await Client.RpcAsync(Expand(row["message"]));
            Assert.Equal(row["reply"], NinePClient.Text(reply));
        }
    }

    [Then(@"^reading fid (\d+) at offset (\d+) with a count of (\d+) gives the stats:$")]
    public async Task ThenStats(uint fid, ulong offset, uint count, Table table)
        => Assert.Equal(table.Rows.Select(row => row["stat"]), NinePClient.Stats(await Client.RpcAsync($"Tread {fid} {offset} {count}")));

    [Then(@"^reading fid (\d+) at offset (\d+) with a count of (\d+) lists (.*)$")]
    public async Task ThenLists(uint fid, ulong offset, uint count, string names)
    {
        byte[] r = await Client.RpcAsync($"Tread {fid} {offset} {count}");
        Assert.StartsWith("Rread", NinePClient.Text(r), StringComparison.Ordinal);
        Assert.Equal(names == "nothing" ? [] : names.Split(' '), NinePClient.Stats(r).Select(stat => stat.Split(' ')[0]));
    }

    [When(@"^the clock reads (\d+)\.(\d) seconds$")]
    public void WhenClock(long seconds, int tenths)
        => context.Clock.SetUtcNow(DateTimeOffset.UnixEpoch.AddSeconds(seconds).AddMilliseconds(tenths * 100));

    [When(@"^the client sends a message of (\d+) bytes of type (\d+)$")]
    public async Task WhenSendsRaw(int size, byte type)
    {
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)size);
        bytes[4] = type;
        await Client.SendBytesAsync(bytes);
    }

    [When(@"^the client sends (\d+) bytes of a message of (\d+) and hangs up$")]
    public async Task WhenSendsShort(int sent, int size)
    {
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)size);
        bytes[4] = 100;
        await Client.SendBytesAsync(bytes[..sent]);
        Client.HangUp();
    }

    [When(@"^the client sends ""(.*)"" without waiting$")]
    public Task WhenSendsOnly(string message) => Client.SendAsync(NinePClient.Message(Expand(message), 1));

    [When("the client stops hearing replies")]
    public void WhenDeaf() => Client.Deaf = true;

    [When("the client hangs up")]
    public void WhenHangsUp() => Client.HangUp();

    [Then(@"^the server hangs up with ""(.*)""$")]
    public async Task ThenHangsUp(string why)
    {
        await Client.Serving.WaitAsync(Bounded.Limit);
        Assert.Equal(why, Client.Conn.Hangup);
    }

    [Then("the server ends the connection without hanging up")]
    public async Task ThenEnds()
    {
        await Client.Serving.WaitAsync(Bounded.Limit);
        Assert.Null(Client.Conn.Hangup);
    }

    [Then(@"^(\d+) authentications? (?:was|were) started and each ended once$")]
    public void ThenConversationsEnded(int count)
        => Assert.Equal(Enumerable.Repeat(1, count), conversations.Select(c => c.Disposed));

    // {n} is a name of n bytes.
    private static string Expand(string message)
        => System.Text.RegularExpressions.Regex.Replace(message, @"\{(\d+)\}", match => new string('n', int.Parse(match.Groups[1].Value)));
}
