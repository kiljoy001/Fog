using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Protocol;
using NinePSharp.Server;

namespace NinePSharp.Fog.Server;

public sealed record FogNinePLimits(int Sessions, int FidsPerSession, int RequestsPerSession,
    uint MessageSize, long SnapshotBytesPerSession, TimeSpan SnapshotLifetime, TimeSpan SessionLifetime);
