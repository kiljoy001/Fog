using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Gefs;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Host;

// gefs's authnew: factotum's rpc file, factotum mounted from /srv/factotum before /mnt where
// /mnt/factotum is not, and p9any started as the server; the conversation of one auth file.
internal sealed class FactotumAuth(Process process, int fd) : IGefsAuth
{
    private readonly Libauth.AuthRpc rpc = new(process, fd);

    public static async Task<IGefsAuth> NewAsync(Process process)
    {
        try
        {
            await process.StatAsync("/mnt/factotum");
        }
        catch (SyscallException)
        {
            await MountFactotumAsync(process);
        }

        int fd;
        try
        {
            fd = await process.OpenAsync("/mnt/factotum/rpc", NinePConstants.ORDWR);
        }
        catch (SyscallException error)
        {
            throw new IOException(error.Message);
        }

        var auth = new FactotumAuth(process, fd);
        if (await auth.rpc.CallAsync("start", "proto=p9any role=server"u8.ToArray()) != Libauth.Ar.Ok)
        {
            await auth.DisposeAsync();
            throw new IOException(auth.rpc.Error);
        }

        return auth;
    }

    public async Task<(GefsAuthResult Result, byte[] Data)> ReadAsync()
        => await rpc.CallAsync("read", []) switch
        {
            Libauth.Ar.Ok => (GefsAuthResult.Ok, rpc.Arg),
            Libauth.Ar.Done => (GefsAuthResult.Done, []),
            Libauth.Ar.Phase => (GefsAuthResult.Phase, []),
            _ => (GefsAuthResult.Other, []),
        };

    public async Task<bool> WriteAsync(byte[] data) => await rpc.CallAsync("write", data) == Libauth.Ar.Ok;

    // auth_getinfo's cuid, or none if it gives no authinfo.
    public async Task<string?> CuidAsync()
    {
        try
        {
            return (await Libauth.AuthGetinfoAsync(rpc)).Cuid;
        }

        // Stryker disable once Block : the method's default is null too
        catch (SyscallException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync() => await process.CloseAsync(fd);

    // if(access("/mnt/factotum", 0) < 0) mount /srv/factotum before /mnt, if it can be opened.
    private static async Task MountFactotumAsync(Process process)
    {
        int srv;
        try
        {
            srv = await process.OpenAsync("/srv/factotum", NinePConstants.ORDWR);
        }
        catch (SyscallException)
        {
            return;
        }

        try
        {
            await process.MountAsync(srv, -1, "/mnt", MountFlags.Before, string.Empty);
        }
        catch (SyscallException)
        {
            // gefs goes on to open the rpc file, which then fails.
        }
    }
}
