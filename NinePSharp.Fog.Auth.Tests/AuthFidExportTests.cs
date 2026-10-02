using Dp9ik;
using NinePSharp.Constants;
using NinePSharp.Fog.Auth.Tests.Support;
using NinePSharp.Messages;
using NinePSharp.Parser;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests;

public sealed class AuthFidExportTests
{
    private static readonly AuthFidOptions Options = new() { AuthId = "fog", AuthDomain = "fog.example" };

    [Fact]
    public void RequiresItsParts()
    {
        Assert.Throws<ArgumentNullException>("inner", () => new AuthFidExport(null!, new NoKeys(), Options));
        Assert.Throws<ArgumentNullException>("keys", () => new AuthFidExport(new RecordingExport(), (IAuthDatabase)null!, Options));
        Assert.Throws<ArgumentNullException>("options", () => new AuthFidExport(new RecordingExport(), new NoKeys(), null!));
    }

    [Fact]
    public async Task MissingNamesCompareAsEmpty()
    {
        var export = new AuthFidExport(new RecordingExport(), new NoKeys(), Options);
        await export.DispatchAsync("s", NinePMessage.NewMsgTauth(new Tauth(1, 1, "glenda", null)), NinePDialect.NineP2000);

        var uname = (Rerror)await export.DispatchAsync("s", NinePMessage.NewMsgTattach(new Tattach(2, 0, 1, null, string.Empty)), NinePDialect.NineP2000);
        var aname = (Rerror)await export.DispatchAsync("s", NinePMessage.NewMsgTattach(new Tattach(3, 0, 1, "glenda", "/")), NinePDialect.NineP2000);

        Assert.Equal("auth uname mismatch: glenda vs ", uname.Ename);
        Assert.Equal("auth aname mismatch:  vs /", aname.Ename);
    }

    private sealed class NoKeys : IAuthDatabase
    {
        public AuthKey? FindKey(string user) => null;

        public bool SetKey(string user, AuthKey key) => false;

        public bool SetSecret(string user, string secret) => false;

        public void Succeed(string user)
        {
        }
    }
}
