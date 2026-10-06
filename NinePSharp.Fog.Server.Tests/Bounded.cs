using System.Security.Cryptography.X509Certificates;
using NinePSharp.Constants;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Parser;

namespace NinePSharp.Fog.Server.Tests;

// A dispatcher that stops answering fails the test instead of hanging it.
internal static class Bounded
{
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    internal static Task<object> DispatchWithinAsync(this FogNinePDispatcher dispatcher, string session, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
        => dispatcher.DispatchAsync(session, message, dialect, certificate).WaitAsync(Wait);

    internal static Task CloseSessionWithinAsync(this FogNinePDispatcher dispatcher, string session)
        => dispatcher.CloseSessionAsync(session).WaitAsync(Wait);
}
