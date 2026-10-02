using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NinePSharp.Fog.Server;

/// <summary>Operator-enrolled node identity. The SPKI pin is not learned from an attach request.</summary>
public sealed record FogNodeEnrollment(string Node, string Boot, string SpkiSha256, string TlsName, bool Enabled = true);
