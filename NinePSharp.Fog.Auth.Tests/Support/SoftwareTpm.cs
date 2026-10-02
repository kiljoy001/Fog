using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tpm2Lib;

namespace NinePSharp.Fog.Auth.Tests.Support;

/// <summary>
/// A swtpm process with its own state directory, started already initialised and powered on. Its
/// command port takes raw TPM 2.0 commands. It runs under a shell that kills it when its standard
/// input closes, so it cannot outlive the test process even when that is killed.
/// </summary>
internal sealed class SoftwareTpm : IDisposable
{
    private readonly Process process;
    private readonly int port;

    private SoftwareTpm(Process process, int port, string stateDirectory)
    {
        this.process = process;
        this.port = port;
        StateDirectory = stateDirectory;
    }

    internal string StateDirectory { get; }

    internal static SoftwareTpm Start()
    {
        string state = Directory.CreateTempSubdirectory("swtpm-").FullName;
        int port = FreePort();
        int control = FreePort();
        string swtpm = $"swtpm socket --tpm2 --tpmstate dir={state} --server type=tcp,port={port},bindaddr=127.0.0.1 " +
            $"--ctrl type=tcp,port={control},bindaddr=127.0.0.1 --flags not-need-init,startup-clear";
        var start = new ProcessStartInfo("sh")
        {
            // The watchdog: when the test process's end of stdin closes, read returns and swtpm is killed.
            ArgumentList = { "-c", $"{swtpm} & tpm=$!; read -r _; kill $tpm; wait $tpm" },
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        Process process = Process.Start(start) ?? throw new InvalidOperationException("swtpm did not start");
        var tpm = new SoftwareTpm(process, port, state);
        tpm.WaitUntilListening();
        return tpm;
    }

    /// <summary>Opens a device for one connection; each keyfs start opens its own.</summary>
    internal Tpm2Device OpenDevice() => new RawTcpDevice(port);

    /// <summary>Counts the persistent object handles the TPM holds.</summary>
    internal int PersistentHandles()
    {
        using Tpm2 tpm = Connect();
        tpm.GetCapability(Cap.Handles, (uint)Ht.Persistent << 24, 64, out ICapabilitiesUnion capabilities);
        return ((HandleArray)capabilities).handle.Length;
    }

    /// <summary>Counts the transient object handles loaded in the TPM.</summary>
    internal int TransientHandles()
    {
        using Tpm2 tpm = Connect();
        tpm.GetCapability(Cap.Handles, (uint)Ht.Transient << 24, 64, out ICapabilitiesUnion capabilities);
        return ((HandleArray)capabilities).handle.Length;
    }

    /// <summary>Extends a PCR with a fixed SHA-256 digest.</summary>
    internal void ExtendPcr(int index)
    {
        using Tpm2 tpm = Connect();
        tpm.PcrExtend(TpmHandle.Pcr(index), [new TpmHash(TpmAlgId.Sha256, Enumerable.Repeat((byte)index, 32).ToArray())]);
    }

    public void Dispose()
    {
        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        process.Dispose();
        Directory.Delete(StateDirectory, recursive: true);
    }

    private Tpm2 Connect()
    {
#pragma warning disable CA2000 // The returned Tpm2 owns the device and disposes it.
        Tpm2Device device = OpenDevice();
#pragma warning restore CA2000
        device.Connect();
        return new Tpm2(device);
    }

    private void WaitUntilListening()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                if (process.HasExited) throw new InvalidOperationException("swtpm exited: " + process.StandardError.ReadToEnd());
                Thread.Sleep(50);
            }
        }

        throw new InvalidOperationException("swtpm did not start listening");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Sends raw TPM commands over swtpm's TCP command port.</summary>
    private sealed class RawTcpDevice(int port) : Tpm2Device
    {
        private TcpClient? client;

        public override void Connect() => client = new TcpClient("127.0.0.1", port);

        public override void Close() => client?.Dispose();

        public override void DispatchCommand(CommandModifier mod, byte[] command, out byte[] response)
        {
            NetworkStream stream = client!.GetStream();
            stream.Write(command);
            var header = new byte[10];
            stream.ReadExactly(header);
            int size = (header[2] << 24) | (header[3] << 16) | (header[4] << 8) | header[5];
            response = new byte[size];
            header.CopyTo(response, 0);
            stream.ReadExactly(response, 10, size - 10);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) client?.Dispose();
            base.Dispose(disposing);
        }
    }
}
