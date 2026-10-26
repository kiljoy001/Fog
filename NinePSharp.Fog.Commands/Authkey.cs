namespace NinePSharp.Fog.Commands;

// authsrv.h's Authkey, as dp9ik.net's AuthKey holds it, with the user authpak_hash hashed it for, so
// that a copy hashes alike.
public sealed class Authkey
{
    private Authkey(Dp9ik.AuthKey k) => K = k;

    public Dp9ik.AuthKey K { get; }

    public string? Hashed { get; private set; }

    // passtokey.
    public static Authkey Passtokey(string password) => new(Dp9ik.AuthKey.FromPassword(password));

    // A key of the DES and AES keys given.
    public static Authkey FromKeys(byte[] des, byte[] aes) => new(Dp9ik.AuthKey.FromKeys(des, aes));

    // authpak_hash.
    public void Hash(string user)
    {
        K.ApplyAuthPakHash(user);
        Hashed = user;
    }

    // memmove of an Authkey: the same DES and AES keys, and hash.
    public Authkey Copy()
    {
        var copy = FromKeys(K.DesKey, K.AesKey);
        if (Hashed is not null)
        {
            copy.Hash(Hashed);
        }

        return copy;
    }
}
