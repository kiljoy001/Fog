using NinePSharp.Constants;
using NinePSharp.Namespaces.Orleans;
using Orleans.Runtime;

namespace NinePSharp.Fog.Namespaces;

/// <summary>
/// The host's shared root directory tree. As in Plan 9 namespace(4), its directories are
/// unwritable placeholders for mounts; only the host adds entries, never a 9P client.
/// </summary>
public interface IFogRootGrain : IAncestryResourceGrain
{
    /// <summary>Creates /, /mnt, /bin and /n once.</summary>
    Task InitializeAsync();

    /// <summary>Adds a directory or file whose parent exists, or returns the existing entry of that kind.</summary>
    Task<ResourceHandleModel> CreateEntryAsync(string path, bool directory);

    /// <summary>Returns the entry at an absolute path, or null.</summary>
    Task<ResourceHandleModel?> LookupAsync(string path);
}
