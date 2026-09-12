using NinePSharp.Fog.Server;
using Xunit;

namespace NinePSharp.Fog.Server.Tests;

public sealed class NodePolicyTests
{
    [Fact]
    public void EpochReplacementFencesEveryPartOfTheAttachedPrincipal()
    {
        using var fixture = new ControlFixture();
        var principal = fixture.Policy.Attach("worker", fixture.NodeCertificate);
        Assert.Equal("node:worker:" + new string('1', 64) + ":1", principal.Owner);
        Assert.Equal(1UL, fixture.Policy.Epoch);
        Assert.True(fixture.Policy.IsCurrentOwner(principal.Owner));
        Assert.False(fixture.Policy.IsCurrentOwner("worker"));
        Assert.False(fixture.Policy.IsCurrentOwner(principal.Owner + "0"));
        fixture.Policy.Check(principal, fixture.NodeCertificate);
        foreach (var invalid in new[]
        {
            principal with { Node = "unknown" }, principal with { Node = "other" },
            principal with { Boot = new string('0', 64) }, principal with { PolicyEpoch = 0 },
            principal with { SpkiSha256 = new string('0', 64) },
        }) Denied(() => fixture.Policy.Check(invalid, fixture.NodeCertificate));
        Denied(() => fixture.Policy.Check(principal, null));
        Denied(() => fixture.Policy.Check(principal, fixture.OtherCertificate));
        Denied(() => fixture.Policy.Attach("unknown", fixture.NodeCertificate));
        Denied(() => fixture.Policy.Attach("worker", null));
        Assert.True(fixture.Policy.AuthenticateCertificate(fixture.NodeCertificate));
        Assert.False(fixture.Policy.AuthenticateCertificate(fixture.ServerCertificate));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Policy.Replace(1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Policy.Replace(0, []));
        fixture.Policy.Replace(2, [new("worker", new string('3', 64), principal.SpkiSha256, "worker.test")]);
        Denied(() => fixture.Policy.Check(principal, fixture.NodeCertificate));
        Assert.False(fixture.Policy.IsCurrentOwner(principal.Owner));
        var next = fixture.Policy.Attach("worker", fixture.NodeCertificate);
        Assert.Equal(new string('3', 64), next.Boot);
        Assert.Equal(2UL, next.PolicyEpoch);
        Assert.True(fixture.Policy.IsCurrentOwner(next.Owner));
        fixture.Policy.Replace(3, [new("worker", next.Boot, next.SpkiSha256, "worker.test", false)]);
        Denied(() => fixture.Policy.Attach("worker", fixture.NodeCertificate));
        Denied(() => fixture.Policy.Check(next with { PolicyEpoch = 3 }, fixture.NodeCertificate));
        Assert.False(fixture.Policy.AuthenticateCertificate(fixture.NodeCertificate));
        Assert.False(fixture.Policy.IsCurrentOwner((next with { PolicyEpoch = 3 }).Owner));
    }

    [Fact]
    public void PolicyOwnsEnrollmentAndRejectsDuplicateIdentities()
    {
        using var certificate = ControlFixture.Certificate("worker.test");
        var enrollment = new FogNodeEnrollment("worker", new string('a', 64), FogNodePolicy.SpkiPin(certificate), "worker.test");
        var registrations = new[] { enrollment };
        var policy = new FogNodePolicy(1, registrations);
        registrations[0] = enrollment with { Enabled = false };
        Assert.True(policy.AuthenticateCertificate(certificate));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FogNodePolicy(0, []));
        Assert.Throws<ArgumentNullException>(() => new FogNodePolicy(1, null!));
        Assert.Throws<ArgumentException>(() => new FogNodePolicy(1, [enrollment, enrollment]));
        Assert.Throws<ArgumentException>(() => new FogNodePolicy(1, [enrollment, enrollment with { Node = "other" }]));
        Assert.Throws<ArgumentException>(() => policy.Replace(2, [enrollment with { Node = "." }]));
        Assert.Equal(1UL, policy.Epoch);
        Assert.True(policy.AuthenticateCertificate(certificate));
    }

    [Theory]
    [InlineData("node", "")]
    [InlineData("node", ".")]
    [InlineData("node", "..")]
    [InlineData("node", "bad/name")]
    [InlineData("node", "é")]
    [InlineData("boot", "a")]
    [InlineData("pin", "G")]
    [InlineData("tls", "")]
    [InlineData("tls", "long")]
    public void InvalidEnrollmentFailsBeforeReplacingPolicy(string field, string value)
    {
        var enrollment = new FogNodeEnrollment("worker", new string('a', 64), new string('b', 64), "worker.test");
        var invalid = field switch
        {
            "node" => enrollment with { Node = value },
            "boot" => enrollment with { Boot = value },
            "pin" => enrollment with { SpkiSha256 = new string(value[0], 64) },
            _ => enrollment with { TlsName = value == "long" ? new string('a', 254) : value },
        };
        Assert.Throws<ArgumentException>(() => new FogNodePolicy(1, [invalid]));
    }

    [Fact]
    public void CertificateNameAndValidityAreRequiredInAdditionToItsPin()
    {
        using var certificate = ControlFixture.Certificate("worker.test");
        var registration = new FogNodeEnrollment("worker", new string('a', 64), FogNodePolicy.SpkiPin(certificate), "wrong.test");
        Assert.False(new FogNodePolicy(1, [registration]).AuthenticateCertificate(certificate));
        var clock = new UtcClock { Now = certificate.NotBefore.ToUniversalTime() };
        var policy = new FogNodePolicy(1, [registration with { TlsName = "worker.test" }], clock);
        Assert.True(policy.AuthenticateCertificate(certificate));
        clock.Now = clock.Now.AddTicks(-1);
        Assert.False(policy.AuthenticateCertificate(certificate));
        clock.Now = certificate.NotAfter.ToUniversalTime();
        Assert.False(policy.AuthenticateCertificate(certificate));
        clock.Now = clock.Now.AddTicks(-1);
        Assert.True(policy.AuthenticateCertificate(certificate));
    }

    private static void Denied(Action check) => Assert.Equal("denied", Assert.Throws<FogException>(check).Code);

    [Fact]
    public void ExactSanNamesAndIdentifierBoundariesAreEnforced()
    {
        using var wildcard = ControlFixture.Certificate("*.example.test");
        using var commonName = ControlFixture.Certificate("worker.test", includeSan: false);
        Assert.False(new FogNodePolicy(1, [new("worker", new string('a', 64), FogNodePolicy.SpkiPin(wildcard), "worker.example.test")]).AuthenticateCertificate(wildcard));
        Assert.False(new FogNodePolicy(1, [new("worker", new string('a', 64), FogNodePolicy.SpkiPin(commonName), "worker.test")]).AuthenticateCertificate(commonName));
        string name = new string('a', 60) + "0._-";
        var entry = new FogNodeEnrollment(name, new string('0', 64), FogNodePolicy.SpkiPin(wildcard), new string('x', 253));
        _ = new FogNodePolicy(1, [entry]);
        Assert.Equal("Invalid node enrollment.", Assert.Throws<ArgumentException>(() => new FogNodePolicy(1, [entry with { Node = name + "a" }])).Message);
    }
    private sealed class UtcClock : TimeProvider
    {
        internal DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
