namespace NinePSharp.Fog;

/// <summary>A bounded protocol error. Never contains input, secrets or exception details.</summary>
public sealed class FogException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
