using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Fog.Namespaces.Tests.Support;
using NinePSharp.Fog.Server;
using NinePSharp.Messages;
using NinePSharp.Namespaces;
using NinePSharp.Namespaces.Authorization;
using NinePSharp.Namespaces.Orleans;
using NinePSharp.Namespaces.Orleans.Server;
using NinePSharp.Parser;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Namespaces.Tests.Steps;

[Binding]
public static class FogNamespaceHooks
{
    [BeforeTestRun]
    public static void StartCluster() => FogNamespaceCluster.Start();

    [AfterTestRun]
    public static void StopCluster() => FogNamespaceCluster.Stop();
}
