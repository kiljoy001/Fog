using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Fog.Server;

if (args.Length < 2)
{
    Console.Error.WriteLine("init DIR | serve DIR PORT BUNDLE [BWRAP] | submit DIR HOST PORT EXPRESSION [OPERATION VARIABLE] | result/status/start/cancel/release DIR HOST PORT ID | probe BUNDLE [BWRAP]");
    return 2;
}

try
{
    if (args[0] == "init") { Initialize(args[1]); return 0; }
    if (args[0] == "serve") { await Serve(args); return 0; }
    if (args[0] == "probe")
    {
        var runner = new LinuxMathRunner(args[1], args.Length > 2 ? args[2] : "/usr/bin/bwrap", Console.Error.WriteLine);
        var outcome = await runner.RunAsync(new("solve", "x", 5000, 15000, 268435456, 65536), "x^2-4"u8.ToArray(), CancellationToken.None);
        Console.WriteLine($"error={outcome.Error ?? "none"} cpu_ms={outcome.CpuMilliseconds}");
        Console.Write(Encoding.UTF8.GetString(outcome.Result));
        return outcome.Error is null ? 0 : 1;
    }
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
    await using var client = await Connect(args[1], args[2], int.Parse(args[3]), deadline.Token);
    if (args[0] == "submit")
    {
        string id = await client.CloneAsync(deadline.Token);
        // Print and flush the identity before any effect: a lost start reply never requires a new job.
        Console.WriteLine(id);
        Console.Out.Flush();
        string operation = args.Length > 5 ? args[5] : "solve";
        string? variable = args.Length > 6 ? args[6] : operation is "solve" or "differentiate" ? "x" : null;
        await client.UploadAsync(id, new(operation, variable, 5000, 15000, 268435456, 65536), Encoding.UTF8.GetBytes(args[4]), deadline.Token);
        await client.ControlAsync(id, "start", deadline.Token);
    }
    else if (args[0] == "result") Console.Write(Encoding.UTF8.GetString(await client.WaitAsync(args[4], deadline.Token)));
    else if (args[0] == "status")
    {
        foreach (var field in await client.StatusAsync(args[4], deadline.Token)) Console.WriteLine($"{field.Key}={field.Value}");
    }
    else await client.ControlAsync(args[4], args[0], deadline.Token);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static void Initialize(string directory)
{
    if (Directory.Exists(directory)) throw new IOException("Choose a new credential directory.");
    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    foreach (string node in new[] { "worker", "client" })
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + node + ".fog.test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(node + ".fog.test");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
        File.WriteAllBytes(Path.Combine(directory, node + ".pfx"), certificate.Export(X509ContentType.Pfx));
        File.SetUnixFileMode(Path.Combine(directory, node + ".pfx"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllBytes(Path.Combine(directory, node + ".cer"), certificate.Export(X509ContentType.Cert));
    }
    File.WriteAllText(Path.Combine(directory, "client.boot"), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
}

static async Task Serve(string[] args)
{
    using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(args[1], "worker.pfx"), null);
    using var clientCertificate = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(args[1], "client.cer"));
    var policy = new FogNodePolicy(1, [new("client", File.ReadAllText(Path.Combine(args[1], "client.boot")),
        FogNodePolicy.SpkiPin(clientCertificate), "client.fog.test")]);
    var runner = new LinuxMathRunner(args[3], args.Length > 4 ? args[4] : "/usr/bin/bwrap", Console.Error.WriteLine);
    await using var tree = new FogJobFileTree(runner, policy.IsCurrentOwner, capacity: 4);
    var dispatcher = new FogNinePDispatcher(tree, policy, new(16, 32, 8, 8192, 262144, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)));
    await using var listener = new FogNodeListener(new IPEndPoint(IPAddress.Loopback, int.Parse(args[2])), certificate,
        policy, dispatcher, NullLogger.Instance, 16, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
    using var stopped = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.Cancel(); };
    using var signal = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,
        context => { context.Cancel = true; stopped.Cancel(); });
    listener.Start();
    Console.WriteLine($"Listening on {listener.LocalEndpoint}; provider={FogMathJob.ProviderVersion}");
    Console.Out.Flush();
    try { await Task.Delay(Timeout.Infinite, stopped.Token); }
    catch (OperationCanceledException) { }
}

static async Task<FogJobClient> Connect(string directory, string host, int port, CancellationToken cancellation)
{
    using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directory, "client.pfx"), null);
    using var workerCertificate = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, "worker.cer"));
    IPAddress address = IPAddress.TryParse(host, out var numeric) ? numeric : (await Dns.GetHostAddressesAsync(host, cancellation))[0];
    var tls = await FogTlsClient.ConnectAsync(new IPEndPoint(address, port), "worker.fog.test",
        FogNodePolicy.SpkiPin(workerCertificate), certificate, cancellation);
    return await FogJobClient.ConnectAsync(tls, "client", cancellation);
}
