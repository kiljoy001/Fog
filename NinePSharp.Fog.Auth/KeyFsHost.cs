using Dp9ik;
using Org.BouncyCastle.Security;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// A running keyfs: its database unsealed with the TPM-held storage key and its tree served on the
/// owner-only admin socket. Disposing stops the listener and removes the socket.
/// </summary>
public sealed class KeyFsHost : IAsyncDisposable
{
    internal const string InvalidRecoveryPhrase = "keyfs: invalid recovery phrase";
    internal const string NoSealedKey = "keyfs: no sealed storage key; recover the database with its phrase";
    internal const string DatabaseMissing = "keyfs: database missing";
    private const int StorageKeyLength = 32;
    private static readonly SecureRandom Random = new();
    private readonly SecretBuffer storageKey;
    private readonly KeyFsAdminListener listener;

    private KeyFsHost(SecretBuffer storageKey, KeyFsAdminListener listener, KeyFsDispatcher dispatcher, string? newRecoveryPhrase)
    {
        this.storageKey = storageKey;
        this.listener = listener;
        Dispatcher = dispatcher;
        NewRecoveryPhrase = newRecoveryPhrase;
    }

    internal KeyFsDispatcher Dispatcher { get; }

    /// <summary>Looks up a user's keys as authsrv's findkey reads them from keyfs.</summary>
    internal AuthKey? FindKey(string user) => Dispatcher.FindKey(user);

    /// <summary>Gets the number of admin connections being served.</summary>
    internal int AdminConnections => listener.ConnectionCount;

    /// <summary>
    /// Gets the recovery phrase of a database this start created, or null when it opened an existing one.
    /// It is shown once and never stored.
    /// </summary>
    public string? NewRecoveryPhrase { get; }

    /// <summary>Opens the database, creating it and sealing a new storage key when none exists, and starts the admin listener.</summary>
    public static Task<KeyFsHost> StartAsync(KeyFsOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareStateDirectory(options);
        byte[]? sealedKey = options.Files.Read(options.SealedKeyPath);
        byte[]? file = options.Files.Read(options.DatabasePath);
        if (sealedKey is null)
        {
            if (file is not null) throw new KeyFsException(NoSealedKey);
            return Task.FromResult(Create(options));
        }

        if (file is null) throw new KeyFsException(DatabaseMissing);
        byte[] storageKey = StorageKeySeal.Unseal(options.OpenTpm, sealedKey);
        return Task.FromResult(Serve(options, storageKey, KeyFsStore.Open(storageKey, file), newRecoveryPhrase: null));
    }

    /// <summary>Opens the database with its recovery phrase, seals the storage key to this host's TPM and starts the admin listener.</summary>
    public static Task<KeyFsHost> RecoverAsync(KeyFsOptions options, string recoveryPhrase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recoveryPhrase);
        cancellationToken.ThrowIfCancellationRequested();
        if (!RecoveryPhrase.TryDecode(recoveryPhrase, out byte[] storageKey)) throw new KeyFsException(InvalidRecoveryPhrase);
        PrepareStateDirectory(options);
        byte[] file = options.Files.Read(options.DatabasePath) ?? throw new KeyFsException(DatabaseMissing);
        // The phrase must open this database before anything is sealed.
        KeyDatabase database = KeyFsStore.Open(storageKey, file);
        options.Files.Replace(options.SealedKeyPath, StorageKeySeal.Seal(options.OpenTpm, storageKey));
        return Task.FromResult(Serve(options, storageKey, database, newRecoveryPhrase: null));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        using SecretBuffer key = storageKey;
        await listener.DisposeAsync();
    }

    private static KeyFsHost Create(KeyFsOptions options)
    {
        var storageKey = new byte[StorageKeyLength];
        Random.NextBytes(storageKey);
        var database = new KeyDatabase();
        // The database is written before its key is sealed: an interrupted creation leaves no
        // sealed key, so the next start does not open a database nobody holds the phrase for.
        options.Files.Replace(options.DatabasePath, KeyFsStore.Seal(storageKey, database.Encode()));
        options.Files.Replace(options.SealedKeyPath, StorageKeySeal.Seal(options.OpenTpm, storageKey));
        return Serve(options, storageKey, database, RecoveryPhrase.Encode(storageKey));
    }

    private static KeyFsHost Serve(KeyFsOptions options, byte[] storageKey, KeyDatabase database, string? newRecoveryPhrase)
    {
        var dispatcher = new KeyFsDispatcher(database,
            next => options.Files.Replace(options.DatabasePath, KeyFsStore.Seal(storageKey, next.Encode())), options.Time);
        return new KeyFsHost(new SecretBuffer(storageKey), KeyFsAdminListener.Start(options.SocketPath, dispatcher), dispatcher, newRecoveryPhrase);
    }

    private static void PrepareStateDirectory(KeyFsOptions options)
    {
        // Created with the process umask, then narrowed whether it was just created or already existed.
        Directory.CreateDirectory(options.StateDirectory);
        File.SetUnixFileMode(options.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
