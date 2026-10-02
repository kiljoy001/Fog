using NinePSharp.Constants;
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
}
