using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// srvopen returns the channel posted in #s instead of opening one; the process installs it.
internal sealed class SrvOpenException(DescriptorLease posted) : Exception
{
    public DescriptorLease Posted { get; } = posted;
}
