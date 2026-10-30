using System.Security.Cryptography;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// secstore's cryptography against answers computed apart from it, as Fog talking to Fog cannot
// show a mistake both ends make alike: RC4's published vectors, AES-CBC as .NET's CBC and the
// exclusive-or of the encrypted IV give it, and SConn's records and PAK's messages as a Python
// script computes them from pak.c's arithmetic, with genrandom's bytes chosen.
public sealed class SecstoreKnownAnswerTests : IAsyncLifetime
{
    private const string M = "MuES4FHkHCu7K1L8szRRh1DTV+pc2WV3A8wgr2QAfITK+yLH0Lh9rpr3o3G577H6rcOWJXUTQlH4czd831q7ITIaHLHw0RVuwij0P/8tuj4rqI7ho1zR3PZ9oPYCyYiLuCSTAVfqUghIEvOuSgTrTDhBcKFCTGITclsaKm/bLj8=";
    private const string Mu = "eVWD1tS6d7dDpwP7lIukJECNsd11Rlu0/tPPtyQarYUaQ7zJDM3qc6qQXta7OVgMqDJymRaUoDbWDrE8AB4dfx+/zi9PJS/ZAW+sQSE5SdQHn3Kg2At9tE+dCYoV0gYYGl3VXAr5FA5a4uHFAF1HLKOxZ/S3F+QKhbXnekyL31k=";
    private const string K = "rxm0u5+mwspY73vFS6MwqNgX7lA=";
    private const string Kc = "LLZntOtleRol2f/Q5MnNGjxGlW8=";
    private SecstoreHarness h = null!;

    public async Task InitializeAsync() => h = await SecstoreHarness.StartAsync();

    public async Task DisposeAsync()
    {
        Authsrv.Random.Value = null;
        await h.DisposeAsync();
    }

    [Theory]
    [InlineData("Key", "Plaintext", "BBF316E8D940AF0AD3")]
    [InlineData("Wiki", "pedia", "1021BF0420")]
    [InlineData("Secret", "Attack at dawn", "45A01F645FC35B383552544B9BF5")]
    public void Rc4GivesThePublishedStreams(string key, string plain, string cipher)
    {
        byte[] p = System.Text.Encoding.ASCII.GetBytes(plain);
        new Rc4(System.Text.Encoding.ASCII.GetBytes(key)).Crypt(p);
        Assert.Equal(cipher, Convert.ToHexString(p));
    }

    [Fact]
    public void Rc4GoesOnFromWhereItStopped()
    {
        byte[] whole = System.Text.Encoding.ASCII.GetBytes("Attack at dawn");
        new Rc4("Secret"u8).Crypt(whole);
        var rc4 = new Rc4("Secret"u8);
        byte[] a = "Attack"u8.ToArray(), b = " at dawn"u8.ToArray();
        rc4.Crypt(a);
        rc4.Crypt(b);
        Assert.Equal(whole, a.Concat(b).ToArray());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(37)]
    [InlineData(48)]
    public void AesCbcIsCbcOverWholeBlocksAndTheEncryptedIvAfter(int length)
    {
        byte[] key = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        byte[] iv = [.. Enumerable.Range(16, 16).Select(i => (byte)i)];
        byte[] data = [.. Enumerable.Range(100, length).Select(i => (byte)i)];
        int whole = length / 16 * 16;
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] expected = [.. aes.EncryptCbc(data.AsSpan(0, whole), iv, PaddingMode.None), .. new byte[length - whole]];
        byte[] last = whole == 0 ? iv : expected[(whole - 16)..whole];
        byte[] pad = aes.EncryptEcb(last, PaddingMode.None);
        for (int i = whole; i < length; i++)
        {
            expected[i] = (byte)(data[i] ^ pad[i - whole]);
        }

        byte[] got = [.. data];
        using (var state = new Aesstate(key, iv))
        {
            state.CbcEncrypt(got);
        }

        Assert.Equal(expected, got);
        using (var state = new Aesstate(key, iv))
        {
            state.CbcDecrypt(got);
        }

        Assert.Equal(data, got);
    }

    [Fact]
    public void AesCbcTakesTheIvsFirstSixteenBytes()
    {
        byte[] data = new byte[16];
        byte[] longer = new byte[16];
        using (var a = new Aesstate(new byte[16], [.. new byte[16], 9, 9]))
        {
            a.CbcEncrypt(data);
        }

        using (var b = new Aesstate(new byte[16], new byte[16]))
        {
            b.CbcEncrypt(longer);
        }

        Assert.Equal(longer, data);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnSConnRecordIsTheDirectionsRc4OfItsDigestAndDataAfterItsCount()
    {
        (int fa, int fb) = await h.Init.PipeAsync();
        var a = new SConn(h.Init, fa);
        byte[] sigma = [.. Enumerable.Range(0, 20).Select(i => (byte)i)];
        a.Secret(sigma, 0);
        await a.WriteAsync("hello");
        await a.WriteAsync("world");
        Assert.Equal("8019914e36ea8625a20ee84c9d89f65a5dfbfc2fcc32d9306832fb8019a24a780d3c7e9a53a9e577fcfb536f25df02fece9b2db596bd", Convert.ToHexString(await Authsrv.ReadnAsync(h.Init, fb, 54)).ToLowerInvariant());
        var b = new SConn(h.Init, fb);
        b.Secret(sigma, 1);
        await b.WriteAsync("hello");
        Assert.Equal("80194e05e525884392d84769488b5972c5fe44e7f6611659c0b640", Convert.ToHexString(await Authsrv.ReadnAsync(h.Init, fa, 27)).ToLowerInvariant());
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnSConnReadsTheRecordsOfTheOtherDirection()
    {
        (int fa, int fb) = await h.Init.PipeAsync();
        var b = new SConn(h.Init, fb);
        b.Secret([.. Enumerable.Range(0, 20).Select(i => (byte)i)], 1);
        await h.Init.WriteAsync(fa, Convert.FromHexString("8019914e36ea8625a20ee84c9d89f65a5dfbfc2fcc32d9306832fb8019a24a780d3c7e9a53a9e577fcfb536f25df02fece9b2db596bd"));
        Assert.Equal((5, "hello"), await b.ReadstrAsync());
        Assert.Equal((5, "world"), await b.ReadstrAsync());
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ThePakClientSendsMForItsRandomXAndProvesItHoldsTheSessionSecret()
    {
        Authsrv.Random.Value = b => b.Fill(1);
        (int fa, int fb) = await h.Init.PipeAsync();
        var client = new SConn(h.Init, fa);
        var server = new SConn(h.Init, fb);
        Task<(int Rc, string? S)> c = Pak.ClientAsync(h.Init, "secstore", false, client, "glenda", "secretpw");
        Assert.Equal($"secstore\tPAK\nC=glenda\nm={M}\n", (await server.ReadstrAsync()).S);
        await server.WriteAsync($"mu={Mu}\nk={K}\nS=the store\n");
        Assert.Equal($"k'={Kc}\n", (await server.ReadstrAsync()).S);
        Assert.Equal((0, "the store"), await c);

        // Its third record, the two of PAK counted.
        await client.WriteAsync("x");
        Assert.Equal("80153e031efb79debaa39d2c18e6ea1da20b27f8ae9a6a", Convert.ToHexString(await Authsrv.ReadnAsync(h.Init, fb, 23)).ToLowerInvariant());
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ThePakServerAnswersMuForItsRandomYAndTheVerifierAndTakesTheClientsProof()
    {
        await h.GlendaAsync();
        Authsrv.Random.Value = b => b.Fill(2);
        (int fa, int fb) = await h.Init.PipeAsync();
        var client = new SConn(h.Init, fa);
        var server = new SConn(h.Init, fb);
        Task<(int Rc, Pw? Pw)> s = Pak.ServerAsync(h.Init, "secstored", server, "the store", $"secstore\tPAK\nC=glenda\nm={M}\n");
        Assert.Equal($"mu={Mu}\nk={K}\nS=the store\n", (await client.ReadstrAsync()).S);
        await client.WriteAsync($"k'={Kc}\n");
        Assert.Equal(0, (await s).Rc);

        // Its second record, its answer counted.
        await server.WriteAsync("x");
        Assert.Equal("801506572a8bfa04bb0cfeb4ec788fc30cef2e54509aed", Convert.ToHexString(await Authsrv.ReadnAsync(h.Init, fa, 23)).ToLowerInvariant());
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task TheRandomXIsReadBigEndian()
    {
        Authsrv.Random.Value = b =>
        {
            for (int i = 0; i < b.Length; i++)
            {
                b[i] = (byte)(i + 1);
            }
        };
        (int fa, int fb) = await h.Init.PipeAsync();
        var client = new SConn(h.Init, fa);
        _ = Pak.ClientAsync(h.Init, "secstore", false, client, "glenda", "secretpw");
        Assert.Equal("secstore\tPAK\nC=glenda\nm=wyV/NvBWZSUsRq9BHq2zkXmmrpYJ1WSsORqq8X2n8wZWYqxoshtZ/yR+AMTsbZD78S0quPGkJAMLMVajWQw5Fr6AYS/q6E8N8k8CxrN7QUo+mU9ER9528pi8GWBRktxFAuRnoL74eXQFP51TVrLZdmgxvMy0w/HQzMD7ExmIpaU=\n", (await new SConn(h.Init, fb).ReadstrAsync()).S);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ARandomXOfNothingIsTakenAsOne()
    {
        Authsrv.Random.Value = b => b.Clear();
        (int fa, int fb) = await h.Init.PipeAsync();
        var client = new SConn(h.Init, fa);
        _ = Pak.ClientAsync(h.Init, "secstore", false, client, "glenda", "secretpw");
        Assert.Equal("secstore\tPAK\nC=glenda\nm=a7zdnMi1xonZTVnH0ovFuQ6169iNeYSGWD0FizDAAn1HvZzpcMZsylDkPe5Bu9gHoZE7n9mI4TQ9BMWR5dFmkI2vGfQqqNIaiQe1XsfTdpqO6fgbDLT8kQEdLm8YhZYQ07W8a6lW5jzGoGiCyOW0zogiHOg5UTIo1q7rPMYj/nI=\n", (await new SConn(h.Init, fb).ReadstrAsync()).S);
    }
}
