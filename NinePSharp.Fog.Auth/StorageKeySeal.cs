using System.Buffers.Binary;
using Tpm2Lib;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// Seals the storage key to the host's TPM: a keyed-hash object under a transient ECC P-256 storage
/// primary in the owner hierarchy, with no PCR policy. The primary is recreated from its fixed
/// template at every start, so no persistent handle is ever used; only the sealed blobs are kept.
/// </summary>
internal static class StorageKeySeal
{
    internal const string CannotUnseal = "keyfs: cannot unseal storage key";

    private const ObjectAttr StorageAttributes = ObjectAttr.Restricted | ObjectAttr.Decrypt | ObjectAttr.FixedTPM
        | ObjectAttr.FixedParent | ObjectAttr.SensitiveDataOrigin | ObjectAttr.UserWithAuth | ObjectAttr.NoDA;

    private static readonly byte[] Magic = "FOGSEAL1"u8.ToArray();

    private static readonly TpmPublic PrimaryTemplate = new(
        TpmAlgId.Sha256,
        StorageAttributes,
        null,
        new EccParms(new SymDefObject(TpmAlgId.Aes, 128, TpmAlgId.Cfb), new NullAsymScheme(), EccCurve.TpmEccNistP256, new NullKdfScheme()),
        new EccPoint());

    private static readonly TpmPublic SealedTemplate = new(
        TpmAlgId.Sha256,
        ObjectAttr.FixedTPM | ObjectAttr.FixedParent | ObjectAttr.UserWithAuth | ObjectAttr.NoDA,
        null,
        new KeyedhashParms(new NullSchemeKeyedhash()),
        new Tpm2bDigestKeyedhash());

    internal static byte[] Seal(Func<Tpm2Device> openTpm, ReadOnlySpan<byte> storageKey)
    {
        using Tpm2 tpm = Connect(openTpm);
        TpmHandle primary = CreatePrimary(tpm);
        try
        {
            TpmPrivate sealedPrivate = tpm.Create(
                primary,
                new SensitiveCreate([], storageKey.ToArray()),
                SealedTemplate,
                null,
                [],
                out TpmPublic sealedPublic,
                out _,
                out _,
                out _);
            byte[] publicBlob = sealedPublic.GetTpmRepresentation();
            byte[] privateBlob = sealedPrivate.GetTpmRepresentation();
            var blob = new byte[Magic.Length + 2 + publicBlob.Length + 2 + privateBlob.Length];
            Magic.CopyTo(blob, 0);
            BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(Magic.Length), (ushort)publicBlob.Length);
            publicBlob.CopyTo(blob, Magic.Length + 2);
            BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(Magic.Length + 2 + publicBlob.Length), (ushort)privateBlob.Length);
            privateBlob.CopyTo(blob, Magic.Length + 2 + publicBlob.Length + 2);
            return blob;
        }
        finally
        {
            tpm.FlushContext(primary);
        }
    }

    internal static byte[] Unseal(Func<Tpm2Device> openTpm, ReadOnlySpan<byte> blob)
    {
        (TpmPublic Public, TpmPrivate Private) sealedKey;
        try
        {
            sealedKey = Parse(blob);
        }
        catch (Exception exception)
        {
            throw new KeyFsException(CannotUnseal, exception);
        }

        try
        {
            using Tpm2 tpm = Connect(openTpm);
            TpmHandle primary = CreatePrimary(tpm);
            try
            {
                TpmHandle sealedObject = tpm.Load(primary, sealedKey.Private, sealedKey.Public);
                try
                {
                    return tpm.Unseal(sealedObject);
                }
                finally
                {
                    tpm.FlushContext(sealedObject);
                }
            }
            finally
            {
                tpm.FlushContext(primary);
            }
        }
        catch (TpmException exception)
        {
            throw new KeyFsException(CannotUnseal, exception);
        }
    }

    internal static (TpmPublic Public, TpmPrivate Private) Parse(ReadOnlySpan<byte> blob)
    {
        if (!blob.StartsWith(Magic))
        {
            throw new InvalidDataException();
        }

        int offset = Magic.Length;
        int publicLength = BinaryPrimitives.ReadUInt16BigEndian(blob[offset..]);
        byte[] publicBlob = blob.Slice(offset + 2, publicLength).ToArray();
        offset += 2 + publicLength;
        int privateLength = BinaryPrimitives.ReadUInt16BigEndian(blob[offset..]);
        byte[] privateBlob = blob.Slice(offset + 2, privateLength).ToArray();
        if (offset + 2 + privateLength != blob.Length)
        {
            throw new InvalidDataException();
        }

        return (Marshaller.FromTpmRepresentation<TpmPublic>(publicBlob), Marshaller.FromTpmRepresentation<TpmPrivate>(privateBlob));
    }

    private static Tpm2 Connect(Func<Tpm2Device> openTpm)
    {
        Tpm2Device device = openTpm();
        device.Connect();
        return new Tpm2(device);
    }

    private static TpmHandle CreatePrimary(Tpm2 tpm)
        => tpm.CreatePrimary(TpmRh.Owner, new SensitiveCreate(), PrimaryTemplate, null, [], out _, out _, out _, out _);
}
