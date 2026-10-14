using Microsoft.Extensions.Time.Testing;

namespace NinePSharp.Fog.Gefs.Tests.Support;

// The device, store and served tree a scenario works on, shared by the steps that reach them.
public sealed class StoreContext
{
    private static readonly Dictionary<string, string[]> Groups = new() { ["bob"] = ["dev"], ["glenda"] = ["sys"] };

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);

    internal MemoryDevice? Device { get; set; }

    internal Store? Store { get; set; }

    internal GefsFs? Files { get; set; }

    internal GefsServer? Server { get; set; }

    // Users are each in a group of their own name; bob is in dev too and glenda in sys.
    public static bool InGroup(string user, string group) => user == group || (Groups.TryGetValue(user, out string[]? groups) && groups.Contains(group));

    public void At(long seconds) => Clock.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(seconds));
}
