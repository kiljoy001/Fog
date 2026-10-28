using System.Text;
using Microsoft.Extensions.Time.Testing;
using NinePSharp.Constants;
using NinePSharp.Messages;
using NinePSharp.Parser;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests;

/// <summary>Dispatcher behaviour that NinePSharp's message validation keeps out of reach over a socket.</summary>
public sealed class KeyFsDispatcherTests
{
    [Fact]
    public async Task A_Message_Outside_9P2000_Is_A_Bad_Fcall_Type()
    {
        var dispatcher = new KeyFsDispatcher(new KeyDatabase(), _ => { }, TimeProvider.System);
        NinePMessage getattr = NinePMessage.NewMsgTgetattr(new Messages.Tgetattr(7, 0, 0x7FF));
        var refused = await Assert.ThrowsAsync<NotSupportedException>(() => dispatcher.DispatchAsync("session", getattr, NinePDialect.NineP2000));
        Assert.Equal("bad fcall type", refused.Message);
    }

    [Fact]
    public async Task A_Walk_Of_More_Than_16_Names_Is_Refused()
    {
        var dispatcher = new KeyFsDispatcher(new KeyDatabase(), _ => { }, TimeProvider.System);
        await dispatcher.DispatchAsync("session", NinePMessage.NewMsgTattach(new Messages.Tattach(1, 0, uint.MaxValue, "admin", string.Empty)), NinePDialect.NineP2000);
        object reply = await dispatcher.DispatchAsync(
            "session",
            NinePMessage.NewMsgTwalk(new Messages.Twalk(2, 0, 1, Enumerable.Repeat("..", 17).ToArray())),
            NinePDialect.NineP2000);
        Assert.Equal("too many path name elements", Assert.IsType<Messages.Rerror>(reply).Ename);
    }

    [Theory]
    [InlineData(0, "ok\n", null)]
    [InlineData(1, "expired\n", "user expired")]
    public async Task An_Account_Expires_Only_After_Its_Expiry_Second(int secondsAgo, string status, string? refusal)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var database = new KeyDatabase();
        database.Add("glenda").Expire = (uint)(time.GetUtcNow().ToUnixTimeSeconds() - secondsAgo);
        var dispatcher = new KeyFsDispatcher(database, _ => { }, time);
        await dispatcher.DispatchAsync("session", NinePMessage.NewMsgTattach(new Tattach(1, 0, uint.MaxValue, "admin", string.Empty)), NinePDialect.NineP2000);

        Assert.Equal(status, Encoding.UTF8.GetString(Assert.IsType<Rread>(await ReadAsync(dispatcher, 1, "status")).Data.Span));
        object key = await ReadAsync(dispatcher, 2, "key");
        Assert.Equal(refusal, key is Rerror error ? error.Ename : null);
    }

    // The reply to reading glenda/name through a new fid.
    private static async Task<object> ReadAsync(KeyFsDispatcher dispatcher, uint fid, string name)
    {
        Assert.IsType<Rwalk>(await dispatcher.DispatchAsync("session", NinePMessage.NewMsgTwalk(new Twalk(2, 0, fid, ["glenda", name])), NinePDialect.NineP2000));
        Assert.IsType<Ropen>(await dispatcher.DispatchAsync("session", NinePMessage.NewMsgTopen(new Topen(3, fid, NinePConstants.OREAD)), NinePDialect.NineP2000));
        return await dispatcher.DispatchAsync("session", NinePMessage.NewMsgTread(new Tread(4, fid, 0, 8192)), NinePDialect.NineP2000);
    }
}
