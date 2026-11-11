using NinePSharp.Fog.Namespaces.Tests.Support;
using Reqnroll;

namespace NinePSharp.Fog.Rc.Tests.Support;

// The 9front terminal that the scenarios running 9front's clients share, booted once for the run
// and put in Fog's auth domain: the live CD does not configure its interface and its /lib/ndb is
// read-only, and cs and factotum are started again so that both see the auth domain. The host's
// port Secstore is forwarded to its 5356.
[Binding]
public sealed class NineFront
{
    public const string Domain = "fog";
    public const int Secstore = 17381;
    private static readonly SemaphoreSlim Booting = new(1, 1);

    internal static NineFrontTerminal? Terminal { get; private set; }

    [AfterTestRun]
    public static async Task StopAsync()
    {
        if (Terminal is not null)
        {
            await Terminal.DisposeAsync();
            Terminal = null;
        }
    }

    internal static async Task<NineFrontTerminal> TerminalAsync(FakeHostNetwork host)
    {
        if (NineFrontTerminal.Unavailable is string reason)
        {
            throw new InvalidOperationException(reason);
        }

        await Booting.WaitAsync();
        try
        {
            if (Terminal is null)
            {
                NineFrontTerminal t = await NineFrontTerminal.BootAsync((host.HostPort(Secstore), 5356));
                await t.RunAsync("ip/ipconfig");
                await t.RunAsync("cp /lib/ndb/local /tmp/ndblocal");
                await t.RunAsync($"echo 'authdom={Domain} auth=tcp!10.0.2.2!{host.HostPort(567)}' >>/tmp/ndblocal");
                await t.RunAsync("bind /tmp/ndblocal /lib/ndb/local");
                await t.RunAsync("ndb/cs");
                await t.RunAsync("auth/factotum");
                Terminal = t;
            }

            return Terminal;
        }
        finally
        {
            Booting.Release();
        }
    }

    // The terminal's factotum holding only this key for the domain.
    internal static async Task KeyAsync(NineFrontTerminal t, string user, string password)
    {
        await t.RunAsync($"echo 'delkey proto=dp9ik dom={Domain}' >/mnt/factotum/ctl >[2]/dev/null");
        await t.RunAsync($"echo 'key proto=dp9ik dom={Domain} user={user} !password={password}' >/mnt/factotum/ctl");
    }
}
