namespace NinePSharp.Fog.Auth;

// A factotum rpc that did not return ok: a failure with its message, or a phase error.
internal sealed class P9anyException(string? message = null, bool isPhaseError = false) : Exception(message)
{
    internal bool IsPhaseError { get; } = isPhaseError;

    // Where factotum answers toosmall, which libauth's auth_rpc gives no message for.
    internal static P9anyException TooSmall() => new("rpc too small");
}
