namespace NinePSharp.Fog.Wasm;

// proc_exit: unwinds the guest's stack from inside the import, out to the thread that runs it.
internal sealed class ProcExitException(uint code) : Exception
{
    public uint Code { get; } = code;
}
