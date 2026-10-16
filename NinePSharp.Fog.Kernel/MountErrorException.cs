namespace NinePSharp.Fog.Kernel;

// An Rerror: the server's own answer, as opposed to the connection failing.
internal sealed class MountErrorException(string message) : IOException(message);
