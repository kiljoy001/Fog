using Dp9ik;

namespace NinePSharp.Fog.Auth;

/// <summary>What authsrv reads from and writes to keyfs.</summary>
internal interface IAuthDatabase
{
    AuthKey? FindKey(string user);

    bool SetKey(string user, AuthKey key);

    bool SetSecret(string user, string secret);

    void Succeed(string user);
}
