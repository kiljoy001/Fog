namespace NinePSharp.Fog.Auth;

/// <summary>A keyfs that cannot start or recover; the message names the cause, as keyfs prints it.</summary>
public sealed class KeyFsException : Exception
{
    /// <summary>Initializes the exception.</summary>
    public KeyFsException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with its cause.</summary>
    public KeyFsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
