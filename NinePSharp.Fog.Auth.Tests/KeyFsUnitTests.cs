using Xunit;

namespace NinePSharp.Fog.Auth.Tests;

public sealed class KeyFsUnitTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("keyfs-unit-").FullName;

    [Fact]
    public void Replace_Writes_An_Owner_Only_File_And_Leaves_No_Scratch_File()
    {
        string path = Path.Combine(directory, "keys");
        KeyFsFiles.Default.Replace(path, [1, 2, 3]);
        KeyFsFiles.Default.Replace(path, [4, 5]);

        Assert.Equal([4, 5], KeyFsFiles.Default.Read(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(["keys"], Directory.EnumerateFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public void A_Failed_Replace_Leaves_No_Scratch_File()
    {
        string path = Path.Combine(directory, "keys");
        Directory.CreateDirectory(path);
        Assert.ThrowsAny<IOException>(() => KeyFsFiles.Default.Replace(path, [1, 2, 3]));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public void Reading_A_Missing_File_Returns_Null() => Assert.Null(KeyFsFiles.Default.Read(Path.Combine(directory, "none")));

    [Fact]
    public void A_Secret_Buffer_Is_Zeroed_When_Released()
    {
        byte[] bytes = [1, 2, 3, 4];
        using (var secret = new SecretBuffer(bytes)) Assert.Same(bytes, secret.Bytes);
        Assert.Equal(new byte[4], bytes);
    }

    [Fact]
    public void The_Wordlist_Must_Exist_And_Hold_2048_Words()
    {
        Assert.Equal("The BIP-39 wordlist resource is missing.",
            Assert.Throws<InvalidOperationException>(() => RecoveryPhrase.ParseWordlist(null)).Message);
        using var short_ = new MemoryStream("abandon\nability\n"u8.ToArray());
        Assert.Equal("The BIP-39 wordlist must hold 2048 words.",
            Assert.Throws<InvalidOperationException>(() => RecoveryPhrase.ParseWordlist(short_)).Message);
    }

    [Fact]
    public void Encoding_A_Wrong_Size_Key_Names_The_Size()
        => Assert.StartsWith("A storage key is 32 bytes.", Assert.Throws<ArgumentException>(() => RecoveryPhrase.Encode(new byte[31])).Message);

    [Fact]
    public async Task The_Host_Checks_Its_Arguments_Before_Any_Work()
    {
        var options = new KeyFsOptions { StateDirectory = Path.Combine(directory, "state"), SocketPath = Path.Combine(directory, "s") };
        Assert.Equal("options", (await Assert.ThrowsAsync<ArgumentNullException>(() => KeyFsHost.StartAsync(null!, CancellationToken.None))).ParamName);
        Assert.Equal("options", (await Assert.ThrowsAsync<ArgumentNullException>(() => KeyFsHost.RecoverAsync(null!, "phrase", CancellationToken.None))).ParamName);
        Assert.Equal("recoveryPhrase", (await Assert.ThrowsAsync<ArgumentNullException>(() => KeyFsHost.RecoverAsync(options, null!, CancellationToken.None))).ParamName);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => KeyFsHost.StartAsync(options, cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => KeyFsHost.RecoverAsync(options, "phrase", cancelled.Token));
        Assert.False(Directory.Exists(options.StateDirectory));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
