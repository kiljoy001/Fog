using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using NinePSharp.Fog;
using NinePSharp.Fog.Symbolics;

// Private, bounded one-request process protocol; never listen on a network socket.
try
{
    LinuxSeal.Apply();
    var request = RequestReader.Read();
    byte[] result = MathEngine.Execute(request.Source, request.Operation, request.Variable, request.OutputBytes);
    using var output = Console.OpenStandardOutput();
    output.Write(result);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is FogException known ? known.Code : "math-result");
    return 1;
}

[ExcludeFromCodeCoverage]
internal static class RequestReader
{
    internal static MathRequest Read()
    {
        using var input = Console.OpenStandardInput();
        byte[] bytes = new byte[1500001];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = input.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count == bytes.Length) throw new FogException("math-size");
        return JsonSerializer.Deserialize<MathRequest>(bytes.AsSpan(0, count)) ?? throw new FogException("math-syntax");
    }
}

public sealed record MathRequest(byte[] Source, string Operation, string? Variable, int OutputBytes);
