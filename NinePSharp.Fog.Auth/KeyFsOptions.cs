using Tpm2Lib;

namespace NinePSharp.Fog.Auth;

/// <summary>Where a keyfs keeps its state and serves its admin attach, and the TPM that seals its storage key.</summary>
public sealed class KeyFsOptions
{
    public required string StateDirectory { get; init; }

    public required string SocketPath { get; init; }

    public Func<Tpm2Device> OpenTpm { get; init; } = () => new LinuxTpmDevice();

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public string DatabasePath => Path.Combine(StateDirectory, "keys");

    public string SealedKeyPath => Path.Combine(StateDirectory, "storage-key.tpm");

    internal IKeyFsFiles Files { get; init; } = KeyFsFiles.Default;
}
