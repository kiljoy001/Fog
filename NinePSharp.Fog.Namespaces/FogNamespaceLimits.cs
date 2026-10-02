using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;

namespace NinePSharp.Fog.Namespaces;

/// <summary>Bounds for one namespace export, with the same meanings as the Fog control export's.</summary>
public sealed record FogNamespaceLimits(int Sessions, int FidsPerSession, int RequestsPerSession, uint MessageSize, TimeSpan SessionLifetime);
