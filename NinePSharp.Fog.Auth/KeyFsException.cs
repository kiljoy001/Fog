namespace NinePSharp.Fog.Auth;

/// <summary>A keyfs that cannot start or recover; the message names the cause, as keyfs prints it.</summary>
public sealed class KeyFsException : Exception
{
    public KeyFsException(string message)
        : base(message)
    {
    }

    public KeyFsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
