using Xunit;

namespace NinePSharp.Fog.Auth.Tests;

/// <summary>The 256-bit English vectors of the reference BIP-39 implementation (trezor/python-mnemonic vectors.json).</summary>
public sealed class RecoveryPhraseTests
{
    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art")]
    [InlineData("7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f", "legal winner thank year wave sausage worth useful legal winner thank year wave sausage worth useful legal winner thank year wave sausage worth title")]
    [InlineData("8080808080808080808080808080808080808080808080808080808080808080", "letter advice cage absurd amount doctor acoustic avoid letter advice cage absurd amount doctor acoustic avoid letter advice cage absurd amount doctor acoustic bless")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo vote")]
    [InlineData("68a79eaca2324873eacc50cb9c6eca8cc68ea5d936f98787c60c7ebc74e6ce7c", "hamster diagram private dutch cause delay private meat slide toddler razor book happy fancy gospel tennis maple dilemma loan word shrug inflict delay length")]
    [InlineData("9f6a2878b2520799a44ef18bc7df394e7061a224d2c33cd015b157d746869863", "panda eyebrow bullet gorilla call smoke muffin taste mesh discover soft ostrich alcohol speed nation flash devote level hobby quick inner drive ghost inside")]
    [InlineData("066dca1a2bb7e8a1db2832148ce9933eea0f3ac9548d793112d9a95c9407efad", "all hour make first leader extend hole alien behind guard gospel lava path output census museum junior mass reopen famous sing advance salt reform")]
    [InlineData("f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f", "void come effort suffer camp survey warrior heavy shoot primary clutch crush open amazing screen patrol group space point ten exist slush involve unfold")]
    public void Encodes_And_Decodes_The_Reference_Vectors(string entropy, string phrase)
    {
        Assert.Equal(phrase, RecoveryPhrase.Encode(Convert.FromHexString(entropy)));
        Assert.True(RecoveryPhrase.TryDecode(phrase, out byte[] key));
        Assert.Equal(Convert.FromHexString(entropy), key);
    }

    [Theory]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")]
    [InlineData("zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo")]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon notaword")]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art art")]
    [InlineData("")]
    public void Rejects_Wrong_Checksums_Lengths_And_Words(string phrase)
    {
        Assert.False(RecoveryPhrase.TryDecode(phrase, out byte[] key));
        Assert.Empty(key);
    }

    [Fact]
    public void Accepts_Extra_Whitespace_And_Capitals_As_People_Type_Them()
    {
        const string typed = "  ZOO zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo\tzoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo  Vote\n";
        Assert.True(RecoveryPhrase.TryDecode(typed, out byte[] key));
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 32), key);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Encodes_Only_32_Byte_Keys(int length)
        => Assert.Throws<ArgumentException>(() => RecoveryPhrase.Encode(new byte[length]));
}
