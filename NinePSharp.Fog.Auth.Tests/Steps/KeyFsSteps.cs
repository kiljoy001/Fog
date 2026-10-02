using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Dp9ik;
using Microsoft.Extensions.Time.Testing;
using NinePSharp.Client;
using NinePSharp.Fog.Auth.Tests.Support;
using NinePSharp.Messages;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests.Steps;

[Binding]
[Scope(Feature = "Fog keeps its authentication database as 9front keyfs does")]
public sealed class KeyFsSteps
{
    private const UnixFileMode ReadableByAll = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const string UserFiles = "key, aeskey, pakhash, secret, log, status, expire, warnings";
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly List<SoftwareTpm> tpms = new();
    private readonly List<string> directories = new();
    private readonly Dictionary<string, byte[]> reads = new(StringComparer.Ordinal);
    private readonly List<byte[]> keptFiles = new();
    private SoftwareTpm tpm = null!;
    private KeyFsOptions options = null!;
    private KeyFsHost? host;
    private KeyFsClient? keyfs;
    private string? recoveryPhrase;
    private string? attemptedPhrase;
    private Exception? failure;
    private IReadOnlyList<Stat> listing = [];
    private byte[]? writtenKey;
    private byte[]? databaseFile;
    private uint staleFid;
    private string? otherPhrase;
    private Socket? otherServer;
    private KeyFsClient? secondClient;
    private KeyFsOptions? otherOptions;
    private KeyFsHost? otherHost;
    private KeyFsDispatcher? stoppedDispatcher;
    private int socketsBeforeStart;

    [When("a keyfs is initialised on an empty software TPM")]
    public static void WhenInitialised()
    {
        // The background initialised this scenario's keyfs on a fresh software TPM.
    }

    [Given("a keyfs whose storage key is sealed by a software TPM")]
    public async Task GivenKeyFs()
    {
        tpm = NewTpm();
        options = Options(tpm, NewDirectory());
        await StartAsync();
        recoveryPhrase = host!.NewRecoveryPhrase;
    }

    [Given(@"^the user ""(.*)"" with password ""(.*)""$")]
    public async Task GivenUser(string user, string password)
    {
        AuthKey key = AuthKey.FromPassword(password);
        await keyfs!.CreateAsync(string.Empty, user, isDirectory: true);
        await keyfs.WriteAsync($"{user}/key", key.DesKey);
        await keyfs.WriteAsync($"{user}/aeskey", key.AesKey);
    }

    [When("the keyfs is restarted on the same TPM")]
    public async Task WhenRestarted()
    {
        await StopAsync();
        socketsBeforeStart = SocketInodes().Count;
        failure = await CatchAsync(StartAsync);
    }

    [Then(@"^the keyfs refuses to start with ""(.*)""$")]
    public void ThenRefusesToStart(string message)
        => Assert.Equal(message, Assert.IsType<KeyFsException>(failure).Message);

    [When("the keyfs root is listed")]
    public async Task WhenRootListed() => listing = await keyfs!.ListAsync(string.Empty);

    [When(@"^""([^""/]+)"" is listed$")]
    public async Task WhenUserListed(string user) => listing = await keyfs!.ListAsync(user);

    [Then(@"^it contains exactly the directory ""(.*)""$")]
    public void ThenContainsDirectory(string name)
        => Assert.Equal([name], listing.Where(entry => IsDirectory(entry)).Select(entry => entry.Name).ToArray());

    [Then(@"^it contains exactly ""(.*)""$")]
    public void ThenContainsFiles(string names)
        => Assert.Equal(Split(names).Order(StringComparer.Ordinal), listing.Select(entry => entry.Name).Order(StringComparer.Ordinal));

    [Then(@"^every entry is owned by ""(.*)"", directories with mode 0777 and files with mode 0666$")]
    public async Task ThenOwnership(string owner)
    {
        var entries = (await keyfs!.ListAsync(string.Empty)).Concat(await keyfs.ListAsync("glenda")).ToList();
        foreach (Stat entry in entries)
        {
            Assert.Equal(owner, entry.Uid);
            Assert.Equal(owner, entry.Gid);
            Assert.Equal(IsDirectory(entry) ? 0x80000000u | 0x1FF : 0x1B6u, entry.Mode);
        }
    }

    [When(@"^""(.*)"", ""(.*)"" and ""(.*)"" are read$")]
    public async Task WhenFilesRead(string first, string second, string third)
    {
        foreach (string path in new[] { first, second, third })
        {
            reads[path] = await keyfs!.ReadAsync(path);
        }
    }

    [Then(@"^they equal the Dp9ik passtokey DES and AES keys and authpak_hash for ""(.*)"" and ""(.*)""$")]
    public void ThenKeysMatchDp9ik(string user, string password)
    {
        AuthKey expected = HashedKey(user, password);
        Assert.Equal(expected.DesKey, reads[$"{user}/key"]);
        Assert.Equal(expected.AesKey, reads[$"{user}/aeskey"]);
        Assert.Equal(expected.PakHash, reads[$"{user}/pakhash"]);
    }

    [When(@"^the AES key of ""(.*)"" is written to ""(.*)""$")]
    public async Task WhenAesKeyWritten(string password, string path)
        => failure = await CatchAsync(() => keyfs!.WriteAsync(path, AuthKey.FromPassword(password).AesKey));

    [Then(@"^""(.*)"" equals the Dp9ik authpak_hash for ""(.*)"" and ""(.*)""$")]
    public async Task ThenPakHashMatches(string path, string user, string password)
        => Assert.Equal(HashedKey(user, password).PakHash, await keyfs!.ReadAsync(path));

    [Then(@"^""(.*)"" equals the AES key of ""(.*)""$")]
    public async Task ThenAesKeyMatches(string path, string password)
        => Assert.Equal(AuthKey.FromPassword(password).AesKey, await keyfs!.ReadAsync(path));

    [When(@"^(\d+) bytes are written to ""(.*)""$")]
    public async Task WhenBytesWritten(int count, string path)
        => failure = await CatchAsync(() => keyfs!.WriteAsync(path, Enumerable.Repeat((byte)'x', count).ToArray()));

    [Then(@"^the write fails with ""(.*)""$")]
    public void ThenWriteFails(string message) => AssertNinePError(message);

    [When(@"^the directory ""(.*)"" is made in the keyfs root$")]
    public async Task WhenDirectoryMade(string name)
        => failure = await CatchAsync(() => keyfs!.CreateAsync(string.Empty, Unescape(name), isDirectory: true));

    [Then(@"^making it fails with ""(.*)""$")]
    public void ThenMakingFails(string message) => AssertNinePError(message);

    [Then(@"^the keyfs root contains the directory ""(.*)""$")]
    public async Task ThenRootContains(string name)
    {
        Assert.Null(failure);
        Assert.Contains(name, (await keyfs!.ListAsync(string.Empty)).Select(entry => entry.Name));
    }

    [When(@"^""(.*)"" is renamed to ""(.*)""$")]
    public async Task WhenRenamed(string user, string name) => failure = await CatchAsync(() => keyfs!.RenameAsync(user, name));

    [Then(@"^the keyfs root contains exactly the directory ""(.*)""$")]
    public async Task ThenRootContainsExactly(string name)
        => Assert.Equal([name], (await keyfs!.ListAsync(string.Empty)).Select(entry => entry.Name).ToArray());

    [When(@"^""([^""]+)"" is removed$")]
    public async Task WhenRemoved(string path)
    {
        if (!path.Contains('/', StringComparison.Ordinal))
        {
            staleFid = await keyfs!.WalkAsync($"{path}/key");
        }

        failure = await CatchAsync(() => keyfs!.RemoveAsync(path));
    }

    [Then("the keyfs root is empty")]
    public async Task ThenRootEmpty() => Assert.Empty(await keyfs!.ListAsync(string.Empty));

    [Then(@"^reading ""(.*)"" through a fid walked before the removal still fails$")]
    public async Task ThenStaleFidFails(string path)
        => Assert.IsType<NinePException>(await CatchAsync(() => keyfs!.ReadFidAsync(staleFid)));

    [Then(@"^making the file ""(.*)"" in the keyfs root fails with ""(.*)""$")]
    public async Task ThenMakingFileFails(string name, string message)
    {
        failure = await CatchAsync(() => keyfs!.CreateAsync(string.Empty, name, isDirectory: false));
        AssertNinePError(message);
    }

    [Then(@"^making anything inside ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenMakingInsideFails(string user, string message)
    {
        foreach (bool isDirectory in new[] { true, false })
        {
            failure = await CatchAsync(() => keyfs!.CreateAsync(user, "inner", isDirectory));
            AssertNinePError(message);
        }
    }

    [Then(@"^removing ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenRemovingFails(string path, string message)
    {
        failure = await CatchAsync(() => keyfs!.RemoveAsync(path));
        AssertNinePError(message);
    }

    [Given(@"^""([^""/]+)"" is (disabled|in purgatory after 10 bad attempts|expired)$")]
    public async Task GivenState(string user, string state)
    {
        switch (state)
        {
            case "disabled":
                await keyfs!.WriteTextAsync($"{user}/status", "disabled");
                break;
            case "expired":
                await keyfs!.WriteTextAsync($"{user}/expire", (time.GetUtcNow().ToUnixTimeSeconds() - 1).ToString(CultureInfo.InvariantCulture));
                break;
            default:
                await WriteRepeatedly($"{user}/log", "bad", 10);
                break;
        }
    }

    [Given(@"^""([^""/]+)"" is disabled, in purgatory and expired$")]
    public async Task GivenAllStates(string user)
    {
        // Writing status clears the bad-attempt count, so the user is disabled first.
        await keyfs!.WriteTextAsync($"{user}/status", "disabled");
        await WriteRepeatedly($"{user}/log", "bad", 10);
        await keyfs.WriteTextAsync($"{user}/expire", (time.GetUtcNow().ToUnixTimeSeconds() - 1).ToString(CultureInfo.InvariantCulture));
    }

    [Then(@"^reading each of ""(.*)"" in ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenReadingEachFails(string files, string user, string message)
    {
        foreach (string file in Split(files))
        {
            failure = await CatchAsync(() => keyfs!.ReadAsync($"{user}/{file}"));
            AssertNinePError(message);
        }
    }

    [Then(@"^reading ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenReadingFails(string path, string message)
    {
        failure = await CatchAsync(() => keyfs!.ReadAsync(path));
        AssertNinePError(message);
    }

    [Given(@"^""(.*)"" reads ""(.*)""$")]
    public async Task GivenReads(string path, string expected)
    {
        await ArrangeReads(path, expected);
        await ThenReads(path, expected);
    }

    [Then(@"^""(.*)"" reads ""(.*)""$")]
    public async Task ThenReads(string path, string expected) => Assert.Equal(expected, await keyfs!.ReadTextAsync(path));

    [Then(@"^the write succeeds and ""(.*)"" reads ""(.*)""$")]
    public async Task ThenWriteSucceedsAndReads(string path, string expected)
    {
        Assert.Null(failure);
        await ThenReads(path, expected);
    }

    [Then(@"^the bytes of ""(.*)"" are ""(.*)""$")]
    public async Task ThenBytes(string path, string expected)
        => Assert.Equal(Encoding.UTF8.GetBytes(Unescape(expected)), await keyfs!.ReadAsync(path));

    [Given(@"^""([^""/]+)"" expires (now|one second ago)$")]
    public async Task GivenExpires(string user, string when)
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        long expire = when == "now" ? now : now - 1;
        await keyfs!.WriteTextAsync($"{user}/expire", expire.ToString(CultureInfo.InvariantCulture));
    }

    [Then(@"^removing the keyfs root fails with ""(.*)""$")]
    public async Task ThenRemovingRootFails(string message)
    {
        failure = await CatchAsync(() => keyfs!.RemoveAsync(string.Empty));
        AssertNinePError(message);
    }

    [Then(@"^writing to ""(.*)"" fails with ""(.*)""$")]
    public async Task ThenWritingFails(string path, string message)
    {
        failure = await CatchAsync(() => keyfs!.WriteRawAsync(path, [1]));
        AssertNinePError(message);
    }

    [Then(@"^writing to the keyfs root fails with ""(.*)""$")]
    public Task ThenWritingRootFails(string message) => ThenWritingFails(string.Empty, message);

    [Given(@"^a fid walked to ""(.*)""$")]
    public async Task GivenWalkedFid(string path) => staleFid = await keyfs!.WalkAsync(path.TrimEnd('/'));

    [When(@"^""([^""/]+)"" is removed through another fid$")]
    public Task WhenRemovedThroughAnotherFid(string user) => keyfs!.RemoveAsync(user);

    [Then(@"^(opening|reading|writing|stat|removing|wstat) through the walked fid fails with ""(.*)""$")]
    public async Task ThenStaleRequestFails(string request, string message)
    {
        NinePClient raw = keyfs!.Raw;
        failure = await CatchAsync(request switch
        {
            "opening" => () => raw.OpenAsync(staleFid, 0),
            "reading" => () => raw.ReadAsync(staleFid, 0, 64),
            "writing" => () => raw.WriteAsync(staleFid, 0, new byte[7]),
            "stat" => () => raw.StatAsync(staleFid),
            "removing" => () => raw.RemoveAsync(staleFid),
            _ => (Func<Task>)(() => raw.WstatAsync(staleFid, KeyFsClient.RenameStat("other"))),
        });
        AssertNinePError(message);
    }

    [When(@"^""(.*)"" is written to ""(.*)"" (\d+) times$")]
    public Task WhenWrittenTimes(string text, string path, int count) => WriteRepeatedly(path, text, count);

    [When(@"^""(.*)"" is written to ""(.*)"" once more$")]
    public Task WhenWrittenOnceMore(string text, string path) => keyfs!.WriteTextAsync(path, text);

    [When(@"^""(.*)"" is written to ""(.*)"" (\d+) more times after that$")]
    public Task WhenWrittenMoreTimes(string text, string path, int count) => WriteRepeatedly(path, text, count);

    [Then(@"^""(.*)"" can be read$")]
    public async Task ThenCanBeRead(string path) => Assert.NotEmpty(await keyfs!.ReadAsync(path));

    [Then(@"^reading ""(.*)"" fails with ""(.*)"" until (\d+) seconds have passed$")]
    public async Task ThenFailsUntil(string path, string message, int seconds)
    {
        time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
        failure = await CatchAsync(() => keyfs!.ReadAsync(path));
        AssertNinePError(message);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.NotEmpty(await keyfs!.ReadAsync(path));
    }

    // Gherkin unescapes \n in a table cell to a newline, so the value may span lines.
    [When(@"^""([\s\S]*)"" is written to ""(.*)""$")]
    public async Task WhenWritten(string text, string path)
        => failure = await CatchAsync(() => keyfs!.WriteTextAsync(path, Unescape(text)));

    [When(@"^the directory ""(.*)"" is made, ""(.*)"" is written and ""(.*)"" is set to ""(.*)""$")]
    public async Task WhenSeveralChanges(string user, string aesPath, string statusPath, string status)
    {
        writtenKey = AuthKey.FromPassword("scott-password").AesKey;
        await keyfs!.CreateAsync(string.Empty, user, isDirectory: true);
        await keyfs.WriteAsync(aesPath, writtenKey);
        await keyfs.WriteTextAsync(statusPath, status);
    }

    [When(@"^""(.*)"" is set to the DES key of ""(.*)""$")]
    public Task WhenDesKeySet(string path, string password) => keyfs!.WriteAsync(path, AuthKey.FromPassword(password).DesKey);

    [Then(@"^""(.*)"" equals the DES key of ""(.*)""$")]
    public async Task ThenDesKeyMatches(string path, string password)
        => Assert.Equal(AuthKey.FromPassword(password).DesKey, await keyfs!.ReadAsync(path));

    [When(@"^""(.*)"" is written (\d+) times, keeping each database file$")]
    public async Task WhenWrittenKeepingFiles(string path, int count)
    {
        for (int round = 0; round < count; round++)
        {
            await keyfs!.WriteTextAsync(path, round.ToString(CultureInfo.InvariantCulture));
            keptFiles.Add(File.ReadAllBytes(options.DatabasePath));
        }
    }

    [Then(@"^the (\d+) database files have (\d+) different nonces$")]
    public void ThenDistinctNonces(int files, int nonces)
    {
        Assert.Equal(files, keptFiles.Count);
        Assert.Equal(nonces, keptFiles.Select(file => Convert.ToHexString(file, 8, 12)).Distinct().Count());
    }

    [When("another keyfs is initialised on another empty software TPM")]
    public async Task WhenAnotherInitialised()
    {
        KeyFsOptions other = Options(NewTpm(), NewDirectory());
        await using KeyFsHost second = await KeyFsHost.StartAsync(other, CancellationToken.None);
        otherPhrase = second.NewRecoveryPhrase;
    }

    [Then("the two recovery phrases differ")]
    public void ThenPhrasesDiffer()
    {
        Assert.NotNull(otherPhrase);
        Assert.NotEqual(recoveryPhrase, otherPhrase);
    }

    [Given("the state directory has mode 0755")]
    [When("that host's state directory has mode 0755")]
    public void GivenOpenStateDirectory()
        => File.SetUnixFileMode(options.StateDirectory, ReadableByAll);

    [When("a keyfs is initialised in a state directory that does not exist yet")]
    public async Task WhenInitialisedInNewDirectory()
    {
        otherOptions = Options(NewTpm(), Path.Combine(NewDirectory(), "state"));
        await using KeyFsHost created = await KeyFsHost.StartAsync(otherOptions, CancellationToken.None);
    }

    [Then("that state directory exists with mode 0700")]
    public void ThenNewDirectoryMode()
        => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(otherOptions!.StateDirectory));

    [When("another keyfs is initialised on another empty software TPM and restarted at once")]
    public async Task WhenAnotherRestartedAtOnce()
    {
        otherOptions = Options(NewTpm(), NewDirectory());
        await (await KeyFsHost.StartAsync(otherOptions, CancellationToken.None)).DisposeAsync();
        otherHost = await KeyFsHost.StartAsync(otherOptions, CancellationToken.None);
    }

    [Then("its keyfs root is empty")]
    public async Task ThenOtherRootEmpty()
    {
        using KeyFsClient other = await KeyFsClient.AttachAsync(otherOptions!.SocketPath);
        Assert.Empty(await other.ListAsync(string.Empty));
    }

    [Then("the failed start leaves no socket open")]
    public void ThenNoSocketLeaked() => Assert.Equal(socketsBeforeStart, SocketInodes().Count);

    [Then("the stopped keyfs holds no connections")]
    public void ThenNoConnections() => Assert.Equal(0, stoppedDispatcher!.SessionCount);

    [Then("the state directory has mode 0700")]
    public void ThenStateDirectoryMode()
        => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(options.StateDirectory));

    [Then("the database file and the sealed key file have mode 0600")]
    public void ThenFileModes()
    {
        foreach (string file in new[] { options.DatabasePath, options.SealedKeyPath })
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    [When(@"^the database file is cut to (\d+) bytes$")]
    public async Task WhenDatabaseCut(int length)
    {
        await StopAsync();
        File.WriteAllBytes(options.DatabasePath, File.ReadAllBytes(options.DatabasePath)[..length]);
    }

    [When(@"^the sealed key file is (emptied|given another magic|extended by one byte|cut short|given a garbled public area)$")]
    public async Task WhenSealedKeyDamaged(string damage)
    {
        await StopAsync();
        byte[] blob = File.ReadAllBytes(options.SealedKeyPath);
        byte[] damaged = damage switch
        {
            "emptied" => [],
            "given another magic" => [.. "FOGSEAL2"u8, .. blob[8..]],
            "extended by one byte" => [.. blob, 0],
            "cut short" => blob[..^1],

            // Keep the framing and lengths, but replace the public area's contents.
            _ => [.. blob[..10], .. Enumerable.Repeat((byte)0xFF, (blob[8] << 8) | blob[9]), .. blob[(10 + ((blob[8] << 8) | blob[9]))..]],
        };
        File.WriteAllBytes(options.SealedKeyPath, damaged);
    }

    [Then("the sealed key's public area is fixedTPM and fixedParent with an empty authPolicy")]
    public void ThenSealAttributes()
    {
        Tpm2Lib.TpmPublic sealedPublic = StorageKeySeal.Parse(File.ReadAllBytes(options.SealedKeyPath)).Public;
        Assert.True(sealedPublic.objectAttributes.HasFlag(Tpm2Lib.ObjectAttr.FixedTPM));
        Assert.True(sealedPublic.objectAttributes.HasFlag(Tpm2Lib.ObjectAttr.FixedParent));
        Assert.Empty(sealedPublic.authPolicy);
    }

    [When(@"^the (sealed key|database) file is deleted$")]
    public async Task WhenFileDeleted(string file)
    {
        await StopAsync();
        File.Delete(file == "database" ? options.DatabasePath : options.SealedKeyPath);
    }

    [When("saving the database starts failing after part of the new file is written")]
    public async Task WhenSavingFails()
    {
        await StopAsync();
        options = Options(tpm, options.StateDirectory, new FailingFiles());
        await StartAsync();
    }

    [Then(@"^the change fails with ""(.*)""$")]
    public void ThenChangeFails(string message) => AssertNinePError(message);

    [Then(@"^""([^""/]+)"" is unchanged$")]
    public async Task ThenUserUnchanged(string user)
    {
        Assert.Equal([user], (await keyfs!.ListAsync(string.Empty)).Select(entry => entry.Name).ToArray());
        Assert.Equal("2", await keyfs.ReadTextAsync($"{user}/warnings"));
        Assert.Equal("ok", await keyfs.ReadTextAsync($"{user}/status"));
        Assert.Equal(AuthKey.FromPassword("glenda-password").AesKey, await keyfs.ReadAsync($"{user}/aeskey"));
    }

    [When("saving works again and the keyfs is restarted on the same TPM")]
    public async Task WhenSavingWorksAgain()
    {
        await StopAsync();
        options = Options(tpm, options.StateDirectory);
        await StartAsync();
    }

    [Then(@"^""(.*)"" holds the written key$")]
    public async Task ThenHoldsWrittenKey(string path) => Assert.Equal(writtenKey, await keyfs!.ReadAsync(path));

    [When("the database file is read from disk")]
    public void WhenDatabaseRead() => databaseFile = File.ReadAllBytes(options.DatabasePath);

    [Then(@"^it does not contain the bytes of ""(.*)"", its DES key or its AES key$")]
    public void ThenDatabaseOpaque(string user)
    {
        AuthKey key = AuthKey.FromPassword("glenda-password");
        foreach (byte[] secret in new[] { Encoding.UTF8.GetBytes(user), key.DesKey, key.AesKey })
        {
            Assert.False(Contains(databaseFile!, secret), $"database contains {Convert.ToHexString(secret)}");
        }
    }

    [When("any single byte of the database file is changed")]
    public async Task WhenEveryByteChanged()
    {
        await StopAsync();
        byte[] original = File.ReadAllBytes(options.DatabasePath);
        for (int index = 0; index < original.Length; index++)
        {
            byte[] tampered = original.ToArray();
            tampered[index] ^= 0x01;
            File.WriteAllBytes(options.DatabasePath, tampered);
            Exception? refused = await CatchAsync(StartAsync);
            Assert.True(
                refused is KeyFsException { Message: "keyfs: database authentication failed" },
                $"byte {index}: {refused?.Message ?? "started"}");
        }

        // Leave one byte changed for the restart that follows.
        original[^1] ^= 0x01;
        File.WriteAllBytes(options.DatabasePath, original);
    }

    [When("the database file and sealed key are moved to a host with a different software TPM")]
    public async Task WhenMovedWithSealedKey()
    {
        await StopAsync();
        MoveToNewHost(includeSealedKey: true);
        failure = await CatchAsync(StartAsync);
    }

    [When(@"^every PCR the software TPM lets locality 0 extend, 0 to 16 and 23, is extended$")]
    public async Task WhenPcrsExtended()
    {
        await StopAsync();
        foreach (int pcr in Enumerable.Range(0, 17).Append(23))
        {
            tpm.ExtendPcr(pcr);
        }
    }

    [When(@"^the keyfs is started and restarted (\d+) times$")]
    public async Task WhenRestartedTimes(int count)
    {
        for (int round = 0; round < count; round++)
        {
            await StopAsync();
            await StartAsync();
        }
    }

    [Then("the software TPM has no persistent or transient handles")]
    public void ThenNoHandles()
    {
        Assert.Equal(0, tpm.PersistentHandles());
        Assert.Equal(0, tpm.TransientHandles());
    }

    [Then("it reports a 24-word BIP-39 recovery phrase for its storage key")]
    public void ThenReportsPhrase()
    {
        Assert.NotNull(recoveryPhrase);
        Assert.Equal(24, recoveryPhrase!.Split(' ').Length);
        Assert.True(RecoveryPhrase.TryDecode(recoveryPhrase, out byte[] key));
        Assert.Equal(32, key.Length);
    }

    [Then("the phrase is not stored by the host")]
    public async Task ThenPhraseNotStored()
    {
        Assert.True(RecoveryPhrase.TryDecode(recoveryPhrase!, out byte[] storageKey));
        await StopAsync();
        foreach (string file in Directory.EnumerateFiles(options.StateDirectory, "*", SearchOption.AllDirectories))
        {
            byte[] contents = File.ReadAllBytes(file);
            Assert.False(Contains(contents, Encoding.UTF8.GetBytes(recoveryPhrase!)), $"{file} holds the phrase");
            Assert.False(Contains(contents, storageKey), $"{file} holds the storage key");
        }

        await StartAsync();
        Assert.Null(host!.NewRecoveryPhrase);
    }

    [Given("the recovery phrase of the database")]
    public void GivenRecoveryPhrase() => Assert.NotNull(recoveryPhrase);

    [When("the database file is moved to a host with a different software TPM")]
    public async Task WhenMovedWithoutSealedKey()
    {
        await StopAsync();
        MoveToNewHost(includeSealedKey: false);
    }

    [Given("the database file on a host with a different software TPM")]
    public Task GivenDatabaseOnNewHost() => WhenMovedWithoutSealedKey();

    [When("the operator recovers the keyfs with the phrase")]
    public async Task WhenRecovered()
    {
        host = await KeyFsHost.RecoverAsync(options, recoveryPhrase!, CancellationToken.None);
        keyfs = await KeyFsClient.AttachAsync(options.SocketPath);
    }

    [When(@"^the operator recovers the keyfs with (a phrase with a wrong checksum word|a valid phrase for another database|a 12-word phrase)$")]
    public async Task WhenRecoveredWith(string phrase)
    {
        string[] words = recoveryPhrase!.Split(' ');
        attemptedPhrase = phrase switch
        {
            "a phrase with a wrong checksum word" => string.Join(' ', words[..^1].Append(WrongChecksumWord(words))),
            "a valid phrase for another database" => RecoveryPhrase.Encode(Enumerable.Repeat((byte)0x5A, 32).ToArray()),
            _ => string.Join(' ', words[..12]),
        };
        failure = await CatchAsync(async () => host = await KeyFsHost.RecoverAsync(options, attemptedPhrase, CancellationToken.None));
    }

    [When("the operator recovers the keyfs with the phrase on a host with no database file")]
    public async Task WhenRecoveredWithoutDatabase()
    {
        await StopAsync();
        tpm = NewTpm();
        options = Options(tpm, NewDirectory());
        failure = await CatchAsync(async () => host = await KeyFsHost.RecoverAsync(options, recoveryPhrase!, CancellationToken.None));
    }

    [Then(@"^recovery fails with ""(.*)""$")]
    public void ThenRecoveryFails(string message)
        => Assert.Equal(message, Assert.IsType<KeyFsException>(failure).Message);

    [Then("no storage key is sealed on that host")]
    public void ThenNothingSealed() => Assert.False(File.Exists(options.SealedKeyPath));

    [Then("the keyfs starts afterwards without the phrase")]
    public async Task ThenStartsWithoutPhrase()
    {
        await StopAsync();
        await StartAsync();
        Assert.Null(host!.NewRecoveryPhrase);
        Assert.Equal(AuthKey.FromPassword("glenda-password").AesKey, await keyfs!.ReadAsync("glenda/aeskey"));
    }

    [When("the keyfs admin listener starts")]
    public async Task WhenListenerStarts()
    {
        // The background already started one; after a shutdown this starts it again.
        if (host is null)
        {
            await StartAsync();
        }
    }

    [Then("it listens on a Unix-domain socket and on no network address")]
    public void ThenUnixOnly()
    {
        Assert.Equal("socket", Stat("%F", options.SocketPath));

        // No TCP or UDP endpoint of this process is in the listening state.
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Concat(IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners());
        Assert.DoesNotContain(listening, endpoint => OwnedByThisProcess(endpoint.Port));
    }

    [Then(@"^the socket file is owned by the host's service account with mode 0600$")]
    public void ThenSocketMode()
    {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(options.SocketPath));
        Assert.Equal(Environment.UserName, Stat("%U", options.SocketPath));
    }

    [Then(@"^a 9P client on the socket attaches and lists ""(.*)"" in the keyfs root$")]
    public async Task ThenClientLists(string user)
    {
        using KeyFsClient other = await KeyFsClient.AttachAsync(options.SocketPath);
        Assert.Contains(user, (await other.ListAsync(string.Empty)).Select(entry => entry.Name));
    }

    [Given("a socket file left at the admin socket path by a process that was killed")]
    public async Task GivenStaleSocket()
    {
        await StopAsync();

        // .NET removes a socket file it bound when the socket is disposed, so the stale file comes
        // from a process that binds, listens and dies without cleaning up.
        var bind = new System.Diagnostics.ProcessStartInfo(
            "python3",
            ["-c", "import os, socket, sys; s = socket.socket(socket.AF_UNIX); s.bind(sys.argv[1]); s.listen(); os._exit(0)", options.SocketPath]);
        using (var process = System.Diagnostics.Process.Start(bind)!)
        {
            process.WaitForExit();
        }

        Assert.Equal("socket", Stat("%F", options.SocketPath));
    }

    [Given("a directory at the admin socket path")]
    public async Task GivenDirectoryAtSocket()
    {
        await StopAsync();
        Directory.CreateDirectory(options.SocketPath);
    }

    [Given("another keyfs serving the admin socket")]
    public async Task GivenAnotherServer()
    {
        await StopAsync();
        otherServer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        otherServer.Bind(new UnixDomainSocketEndPoint(options.SocketPath));
        otherServer.Listen();
    }

    [Given("an admin socket path too long to bind")]
    public async Task GivenLongSocketPath()
    {
        await StopAsync();
        options = Options(tpm, options.StateDirectory, socketPath: Path.Combine(options.StateDirectory, new string('s', 120)));
    }

    [Then(@"^the keyfs refuses to start with ""(.*)"" for the socket path$")]
    public void ThenRefusesForSocket(string message)
        => Assert.Equal(message.Replace("{path}", options.SocketPath, StringComparison.Ordinal), Assert.IsType<KeyFsException>(failure).Message);

    [Given("a second 9P client attached on the admin socket")]
    public async Task GivenSecondClient() => secondClient = await KeyFsClient.AttachAsync(options.SocketPath);

    [Then("the second client's next request fails")]
    public async Task ThenSecondClientFails()
    {
        Exception? next = await CatchAsync(() => secondClient!.ListAsync(string.Empty).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(next is not null and not TimeoutException, next?.ToString() ?? "the request succeeded");
    }

    [Then("a 9P client on the socket attaches")]
    public async Task ThenClientAttaches()
    {
        using KeyFsClient other = await KeyFsClient.AttachAsync(options.SocketPath);
    }

    [When("the host shuts down")]
    public Task WhenShutDown() => StopAsync();

    [Then("the socket file no longer exists")]
    public void ThenSocketGone() => Assert.False(Path.Exists(options.SocketPath));

    [When("a 9P client on the admin socket sends Tauth")]
    public async Task WhenTauth()
    {
        using KeyFsClient other = await KeyFsClient.ConnectAsync(options.SocketPath);
        failure = await CatchAsync(() => other.Raw.AuthAsync(7, Environment.UserName, string.Empty));
    }

    [Then(@"^it receives ""(.*)""$")]
    public void ThenReceives(string message) => AssertNinePError(message);

    [Then("an attach with afid NOFID succeeds")]
    public async Task ThenNofidAttach()
    {
        using KeyFsClient other = await KeyFsClient.AttachAsync(options.SocketPath);
        Assert.NotEmpty(await other.ListAsync(string.Empty));
    }

    // Reqnroll does not dispose IAsyncDisposable bindings, so cleanup is an explicit hook.
    [AfterScenario]
    public async Task CleanUpAsync()
    {
        secondClient?.Dispose();
        otherServer?.Dispose();
        if (otherHost is not null)
        {
            await otherHost.DisposeAsync();
        }

        await StopAsync();
        foreach (SoftwareTpm each in tpms)
        {
            each.Dispose();
        }

        foreach (string directory in directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static AuthKey HashedKey(string user, string password)
    {
        AuthKey key = AuthKey.FromPassword(password);
        key.ApplyAuthPakHash(user);
        return key;
    }

    private static bool IsDirectory(Stat entry) => (entry.Mode & 0x80000000u) != 0;

    private static string[] Split(string list) => list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Unescape(string text) => text.Replace("\\t", "\t", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);

    private static bool Contains(byte[] haystack, byte[] needle)
        => needle.Length > 0 && haystack.AsSpan().IndexOf(needle) >= 0;

    private static string WrongChecksumWord(string[] words)
    {
        foreach (string candidate in new[] { "abandon", "ability", "able", "about", "above" })
        {
            string phrase = string.Join(' ', words[..^1].Append(candidate));
            if (candidate != words[^1] && !RecoveryPhrase.TryDecode(phrase, out _))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("no word with a wrong checksum among the candidates");
    }

    private static bool OwnedByThisProcess(int port) => SocketInodes().Overlaps(ListeningInodes(port));

    private static HashSet<string> SocketInodes()
        => Directory.EnumerateFileSystemEntries("/proc/self/fd")
            .Select(fd => new FileInfo(fd).LinkTarget ?? string.Empty)
            .Where(target => target.StartsWith("socket:[", StringComparison.Ordinal))
            .Select(target => target[8..^1])
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ListeningInodes(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string table in new[] { "/proc/net/tcp", "/proc/net/tcp6", "/proc/net/udp", "/proc/net/udp6" })
        {
            foreach (string line in File.ReadLines(table).Skip(1))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int localPort = Convert.ToInt32(fields[1].Split(':')[1], 16);
                if (localPort == port)
                {
                    inodes.Add(fields[9]);
                }
            }
        }

        return inodes;
    }

    private static string Stat(string format, string path)
    {
        var stat = new System.Diagnostics.ProcessStartInfo("stat", ["-c", format, path]) { RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(stat)!;
        string owner = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return owner;
    }

    private static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    private async Task StartAsync()
    {
        host = await KeyFsHost.StartAsync(options, CancellationToken.None);
        keyfs = await KeyFsClient.AttachAsync(options.SocketPath);
    }

    private async Task StopAsync()
    {
        keyfs?.Dispose();
        keyfs = null;
        if (host is not null)
        {
            stoppedDispatcher = host.Dispatcher;
            await host.DisposeAsync();
        }

        host = null;
    }

    private async Task ArrangeReads(string path, string expected)
    {
        string file = path[(path.IndexOf('/') + 1)..];
        if (file == "log")
        {
            await WriteRepeatedly(path, "bad", int.Parse(expected, CultureInfo.InvariantCulture));
        }
        else
        {
            await keyfs!.WriteTextAsync(path, expected);
        }
    }

    private async Task WriteRepeatedly(string path, string text, int count)
    {
        for (int index = 0; index < count; index++)
        {
            await keyfs!.WriteTextAsync(path, text);
        }
    }

    private void MoveToNewHost(bool includeSealedKey)
    {
        KeyFsOptions previous = options;
        tpm = NewTpm();
        options = Options(tpm, NewDirectory());
        File.Copy(previous.DatabasePath, options.DatabasePath);
        if (includeSealedKey)
        {
            File.Copy(previous.SealedKeyPath, options.SealedKeyPath);
        }
    }

    private SoftwareTpm NewTpm()
    {
        SoftwareTpm created = SoftwareTpm.Start();
        tpms.Add(created);
        return created;
    }

    private string NewDirectory()
    {
        string directory = Directory.CreateTempSubdirectory("keyfs-").FullName;
        directories.Add(directory);
        return directory;
    }

    private KeyFsOptions Options(SoftwareTpm softwareTpm, string directory, IKeyFsFiles? files = null, string? socketPath = null) => new()
    {
        StateDirectory = directory,
        SocketPath = socketPath ?? Path.Combine(directory, "keys.sock"),
        OpenTpm = softwareTpm.OpenDevice,
        Time = time,
        Files = files ?? KeyFsFiles.Default,
    };

    private void AssertNinePError(string message)
        => Assert.Equal(message, Assert.IsType<NinePException>(failure).Message);
}
