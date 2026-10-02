using NinePSharp.Constants;

namespace NinePSharp.Fog.Server;

public sealed record FogFileNode(ulong QidPath, string Name, bool Directory, string Service = "", string Transaction = "", string File = "");
