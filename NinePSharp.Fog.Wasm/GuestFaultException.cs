namespace NinePSharp.Fog.Wasm;

// A host call's access outside guest memory; the call returns FAULT.
internal sealed class GuestFaultException : Exception;
