using NinePSharp.Fog.Auth;

namespace NinePSharp.Fog.Rc.Tests.Support;

// keyfs as authsrv sees it: each user's key from a password, hashed for the user.
internal sealed class PasswordDatabase(Dictionary<string, string> passwords) : IAuthDatabase
{
    public Dp9ik.AuthKey? FindKey(string user)
    {
        if (!passwords.TryGetValue(user, out string? password))
        {
            return null;
        }

        Dp9ik.AuthKey key = Dp9ik.AuthKey.FromPassword(password);
        key.ApplyAuthPakHash(user);
        return key;
    }

    public bool SetKey(string user, Dp9ik.AuthKey key) => false;

    public bool SetSecret(string user, string secret) => false;

    public void Succeed(string user)
    {
    }
}
