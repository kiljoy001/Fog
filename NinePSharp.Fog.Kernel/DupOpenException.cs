namespace NinePSharp.Fog.Kernel;

// dupopen returns descriptor N's channel instead of opening one; the process installs it.
internal sealed class DupOpenException(int descriptor) : Exception
{
    public int Descriptor { get; } = descriptor;
}
