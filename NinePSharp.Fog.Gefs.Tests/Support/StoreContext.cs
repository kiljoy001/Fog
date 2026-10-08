namespace NinePSharp.Fog.Gefs.Tests.Support;

// The device and store a scenario works on, shared by the steps that reach them.
public sealed class StoreContext
{
    internal MemoryDevice? Device { get; set; }

    internal Store? Store { get; set; }
}
