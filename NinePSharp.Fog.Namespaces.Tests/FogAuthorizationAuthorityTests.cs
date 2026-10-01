using NinePSharp.Namespaces.Authorization;
using Xunit;

namespace NinePSharp.Fog.Namespaces.Tests;

public sealed class FogAuthorizationAuthorityTests
{
    [Fact]
    public void ReplacementOnlyMovesToAHigherGeneration()
    {
        AuthorizationPolicy Policy(ulong generation) => new(generation, [new AuthorizationPrincipal("worker")], [], []);
        Assert.Equal("initial", Assert.Throws<ArgumentNullException>(() => new FogAuthorizationAuthority(null!)).ParamName);
        var authority = new FogAuthorizationAuthority(Policy(3));
        Assert.Equal(3UL, authority.Generation);
        Assert.Equal(3UL, authority.Current.Generation);

        Assert.Equal("next", Assert.Throws<ArgumentOutOfRangeException>(() => authority.Replace(Policy(3))).ParamName);
        Assert.Equal("next", Assert.Throws<ArgumentOutOfRangeException>(() => authority.Replace(Policy(2))).ParamName);
        Assert.Equal("next", Assert.Throws<ArgumentNullException>(() => authority.Replace(null!)).ParamName);
        Assert.Equal(3UL, authority.Generation);

        AuthorizationPolicy next = Policy(5);
        authority.Replace(next);
        Assert.Same(next, authority.Current);
        Assert.Equal(5UL, authority.Generation);
    }
}
