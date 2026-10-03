namespace NinePSharp.Fog.Kernel;

public sealed class SyscallException : Exception
{
    public SyscallException(string message)
        : base(message)
    {
    }
}
