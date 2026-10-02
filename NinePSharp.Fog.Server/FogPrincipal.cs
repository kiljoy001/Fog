using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NinePSharp.Fog.Server;

public sealed record FogPrincipal(string Node, string Boot, ulong PolicyEpoch, string SpkiSha256)
{
    public string Owner => $"node:{Node}:{Boot}:{PolicyEpoch}";
}
