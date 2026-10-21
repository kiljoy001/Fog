using System.Net;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// libip as 9front's parseip.c, classmask.c and eipfmt.c have it: each address as parseip reads it
// and %I prints it, each mask as parseipmask reads it and %M prints it.
public sealed class IpTests
{
    [Theory]
    [InlineData("1.2.3.4", 0x01020304L, "1.2.3.4")]
    [InlineData("1", 0x01000000L, "1.0.0.0")]
    [InlineData("10.1", 0x0a000001L, "10.0.0.1")]
    [InlineData("10.1.2", 0x0a000102L, "10.0.1.2")]
    [InlineData("100.1.2", 0x64000102L, "100.0.1.2")]
    [InlineData("172.16", 0xac100000L, "172.16.0.0")]
    [InlineData("172.16.1", 0xac100001L, "172.16.0.1")]
    [InlineData("192.168.1", 0xc0a80100L, "192.168.1.0")]
    [InlineData("012.0x10.1.2", 0x0a100102L, "10.16.1.2")]
    [InlineData("1.2.3.4!tcp", 0x01020304L, "1.2.3.4")]
    [InlineData("255.255.255.255", 0xffffffffL, "255.255.255.255")]
    [InlineData("::1", 6L, "::1")]
    [InlineData(":1", 6L, "::1")]
    [InlineData("::", 6L, "::")]
    [InlineData("1::", 6L, "1::")]
    [InlineData("fe80::1%ether0", 6L, "fe80::1")]
    [InlineData("1:2:3:4:5:6:7:8", 6L, "1:2:3:4:5:6:7:8")]
    [InlineData("1::2:3:4:5:6:7:8", 6L, "1:2:3:4:5:6:7:8")]
    [InlineData("1:2:3:4:5:6:1.2.3.4", 6L, "1:2:3:4:5:6:102:304")]
    [InlineData("::ffff:1.2.3.4", 6L, "1.2.3.4")]
    [InlineData("2001:db8:0:0:1:0:0:1", 6L, "2001:db8::1:0:0:1")]
    [InlineData("0:0:1:0:0:0:1:0", 6L, "0:0:1::1:0")]
    [InlineData("1:0:0:2:0:0:0:3", 6L, "1:0:0:2::3")]
    [InlineData("1:2:3:4:5:6:7:0", 6L, "1:2:3:4:5:6:7::")]
    [InlineData("0:1:2:3:4:5:6:7", 6L, "::1:2:3:4:5:6:7")]
    [InlineData("ffff:0:0:0:0:0:0:0", 6L, "ffff::")]
    public void AnAddressParsesAsParseipReadsItAndPrintsAsPercentIDoes(string text, long value, string printed)
    {
        byte[] ip = [.. Enumerable.Repeat((byte)7, Ip.AddrLen)];
        Assert.Equal(value, Ip.Parse(ip, text));
        Assert.Equal(printed, Ip.Format(ip));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("delete")]
    [InlineData("1.2.3.4x")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1::2::3")]
    [InlineData("12345::1")]
    [InlineData("1:2:3:4:5:6:7:1.2.3.4")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    [InlineData("1:2x")]
    public void AnAddressThatDoesNotParseIsMinusOneAndNoAddress(string text)
    {
        byte[] ip = [.. Enumerable.Repeat((byte)7, Ip.AddrLen)];
        Assert.Equal(-1, Ip.Parse(ip, text));
        Assert.Equal(new byte[Ip.AddrLen], ip);
    }

    [Theory]
    [InlineData("/24", true, 0xffffff00L, "/120")]
    [InlineData("/20", true, 0xfffff000L, "/116")]
    [InlineData("/0", true, 0L, "/96")]
    [InlineData("/-5", true, 0L, "/96")]
    [InlineData("/32", true, 0xffffffffL, "/128")]
    [InlineData("/33", false, 6L, "/33")]
    [InlineData("/64", false, 6L, "/64")]
    [InlineData("/95", false, 6L, "/95")]
    [InlineData("/96", false, 0L, "/96")]
    [InlineData("/97", false, 0x80000000L, "/97")]
    [InlineData("/200", false, 0xffffffffL, "/128")]
    [InlineData("255.255.0.0", true, 0xffff0000L, "/112")]
    [InlineData("255.255.0.0", false, 0xffff0000L, "/112")]
    [InlineData("255.0.255.0", true, 0xff00ff00L, "ffff:ffff:ffff:ffff:ffff:ffff:ff00:ff00")]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ff00::", true, 6L, "/104")]
    [InlineData("ffff::", false, 6L, "/16")]
    public void AMaskParsesAsParseipmaskReadsItAndPrintsAsPercentMDoes(string text, bool v4, long value, string printed)
    {
        byte[] mask = new byte[Ip.AddrLen];
        Assert.Equal(value, Ip.ParseMask(mask, text, v4));
        Assert.Equal(printed, Ip.FormatMask(mask));
    }

    [Theory]
    [InlineData("/64", true)]
    [InlineData("/33", true)]
    [InlineData("ffff::", true)]
    public void AV4MaskThatCannotBeOneIsMinusOne(string text, bool v4)
    {
        Assert.Equal(-1, Ip.ParseMask(new byte[Ip.AddrLen], text, v4));
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xf1, 0, 0, 0 }, "ffff:ffff:ffff:ffff:ffff:ffff:f100::")]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xf0, 0, 0, 1 }, "ffff:ffff:ffff:ffff:ffff:ffff:f000:1")]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x80, 0, 0 }, "/105")]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xfe, 0, 0 }, "/111")]
    [InlineData(new byte[] { 0xfc, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "/6")]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "/0")]
    public void AMaskThatIsNotAPrefixPrintsAsAnAddress(byte[] mask, string printed)
    {
        Assert.Equal(printed, Ip.FormatMask(mask));
    }

    [Theory]
    [InlineData("1.2.3.4", "/104")]
    [InlineData("64.2.3.4", "/104")]
    [InlineData("127.0.0.1", "/104")]
    [InlineData("128.2.3.4", "/112")]
    [InlineData("191.2.3.4", "/112")]
    [InlineData("192.2.3.4", "/120")]
    [InlineData("255.2.3.4", "/120")]
    [InlineData("::1", "/128")]
    [InlineData("2001:db8::1", "/128")]
    [InlineData("fe80::1", "/64")]
    [InlineData("fe80:0:0:1::1", "/128")]
    [InlineData("ff02::1:ff00:1", "/104")]
    [InlineData("ff02::1:fe00:1", "/8")]
    [InlineData("ff02::1", "/8")]
    [InlineData("fe02::1", "/128")]
    public void TheDefaultMaskIsTheClassOfAV4AddressOrTheKindOfAV6One(string address, string mask)
    {
        byte[] ip = new byte[Ip.AddrLen];
        Ip.Parse(ip, address);
        Assert.Equal(mask, Ip.FormatMask(Ip.DefMask(ip)));
    }

    [Theory]
    [InlineData("fe80::1", true)]
    [InlineData("febf::1", true)]
    [InlineData("fec0::1", false)]
    [InlineData("fe40::1", false)]
    [InlineData("ff80::1", false)]
    public void ALinkLocalAddressIsFe80Slash10(string address, bool linklocal)
    {
        byte[] ip = new byte[Ip.AddrLen];
        Ip.Parse(ip, address);
        Assert.Equal(linklocal, Ip.IsV6LinkLocal(ip));
    }

    [Fact]
    public void MaskipKeepsOnlyTheMasksBits()
    {
        byte[] ip = new byte[Ip.AddrLen], mask = new byte[Ip.AddrLen];
        Ip.Parse(ip, "10.20.30.40");
        Ip.ParseMask(mask, "/12", true);
        Assert.Equal("10.16.0.0", Ip.Format(Ip.MaskIp(ip, mask)));
    }

    [Fact]
    public void TheInterfacesAreTheHostsWithTheMaskOfEachAddressAndTheNetworkItGives()
    {
        var host = new FakeHostNetwork();
        host.Ifcs.Clear();
        host.Ifcs.Add(new HostInterface("ether0", [new(IPAddress.Parse("10.20.30.40"), 12), new(IPAddress.Parse("2001:db8::5"), 64)]));
        host.Ifcs.Add(new HostInterface("ether1", []));
        IReadOnlyList<Ipifc> ifcs = Ip.ReadIpifc(host);
        Assert.Equal(["ether0", "ether1"], ifcs.Select(i => i.Dev));
        Assert.Empty(ifcs[1].Lifc);
        Assert.Equal(
            [("10.20.30.40", "/108", "10.16.0.0"), ("2001:db8::5", "/64", "2001:db8::")],
            ifcs[0].Lifc.Select(l => (Ip.Format(l.Ip), Ip.FormatMask(l.Mask), Ip.Format(l.Net))));
    }
}
