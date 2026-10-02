using Tpm2Lib;

namespace NinePSharp.Fog.Auth;

/// <summary>Where a keyfs keeps its state and serves its admin attach, and the TPM that seals its storage key.</summary>
public sealed class KeyFsOptions
{
    /// <summary>Gets the directory holding the encrypted database and the sealed storage key.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>Gets the path of the owner-only Unix-domain socket serving the keyfs tree.</summary>
    public required string SocketPath { get; init; }

    /// <summary>Gets the factory for the TPM that seals the storage key; the default is the kernel resource manager.</summary>
    public Func<Tpm2Device> OpenTpm { get; init; } = () => new LinuxTpmDevice();

    /// <summary>Gets the clock for expiry and purgatory.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Gets the path of the encrypted user database.</summary>
    public string DatabasePath => Path.Combine(StateDirectory, "keys");

    /// <summary>Gets the path of the TPM-sealed storage key.</summary>
    public string SealedKeyPath => Path.Combine(StateDirectory, "storage-key.tpm");

    internal IKeyFsFiles Files { get; init; } = KeyFsFiles.Default;
}
