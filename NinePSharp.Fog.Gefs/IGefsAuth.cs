namespace NinePSharp.Fog.Gefs;

// authnew's AuthRpc: factotum's p9any, as role=server, for one auth file.
public interface IGefsAuth : IAsyncDisposable
{
    // auth_rpc(rpc, "read"): what factotum says, and with ARok what to give the client.
    Task<(GefsAuthResult Result, byte[] Data)> ReadAsync();

    // auth_rpc(rpc, "write") == ARok: what the client wrote, given to factotum.
    Task<bool> WriteAsync(byte[] data);

    // auth_getinfo's cuid, or null when it gives none.
    Task<string?> CuidAsync();
}
