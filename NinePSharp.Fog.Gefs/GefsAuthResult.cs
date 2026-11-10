namespace NinePSharp.Fog.Gefs;

// auth_rpc's answers that authread tells apart: ARok with data, ARdone, ARphase, and the rest.
public enum GefsAuthResult
{
    Ok,
    Done,
    Phase,
    Other,
}
