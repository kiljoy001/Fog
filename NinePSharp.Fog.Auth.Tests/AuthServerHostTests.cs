using System.Net;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests;

public sealed class AuthServerHostTests
{
    [Fact]
    public void Start_Checks_Its_Arguments()
    {
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => AuthServerHost.Start(null!, null!)).ParamName);
        var options = new AuthServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) };
        Assert.Equal("keys", Assert.Throws<ArgumentNullException>(() => AuthServerHost.Start(options, null!)).ParamName);
    }
}
