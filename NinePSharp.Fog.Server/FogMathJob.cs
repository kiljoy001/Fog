using System.Globalization;

namespace NinePSharp.Fog.Server;

/// <summary>Direct-worker demo contract. This is not registration of the complete fog-math-v1 profile.</summary>
public sealed record FogMathJob(string Operation, string? Variable, int CpuMilliseconds, int DeadlineMilliseconds, int MemoryBytes, int OutputBytes)
{
    public const string ProviderVersion = "angouri-2.4.0-demo1";
    public static readonly FogRecordSchema SpecSchema = new("fogjob-v1",
        ["job", "runtime", "provider_version", "source", "p_cpu_ms", "p_operation", "p_variable", "memory_bytes", "deadline_ms", "output_bytes"],
        ["job", "runtime", "provider_version", "source", "p_cpu_ms", "p_operation", "memory_bytes", "deadline_ms", "output_bytes"], ["job"]);
    public static readonly FogRecordSchema StatusSchema = new("fogstatus-v1",
        ["job", "state", "error", "result_bytes", "p_cpu_ms_used"], ["job", "state", "result_bytes"], ["job"]);

    public static FogMathJob Parse(string id, byte[] bytes)
    {
        var rows = SpecSchema.Parse(bytes, 8192, 1);
        if (rows.Count != 1) throw new FogException("invalid-request");
        var row = rows[0];
        if (row["job"] != id || row["runtime"] != "math" || row["provider_version"] != ProviderVersion || row["source"] != $"/compute/{id}/input")
            throw new FogException("invalid-request");
        var job = new FogMathJob(row["p_operation"]!, row["p_variable"], Number(row, "p_cpu_ms", 1, 30000),
            Number(row, "deadline_ms", 100, 60000), Number(row, "memory_bytes", 134217728, 1073741824), Number(row, "output_bytes", 256, 65536));
        job.Validate();
        return job;
    }

    public byte[] Serialize(string id)
    {
        Validate();
        return SpecSchema.Serialize([new Dictionary<string, string?>
        {
            ["job"] = id, ["runtime"] = "math", ["provider_version"] = ProviderVersion, ["source"] = $"/compute/{id}/input",
            ["p_cpu_ms"] = Decimal(CpuMilliseconds), ["p_operation"] = Operation, ["p_variable"] = Variable,
            ["memory_bytes"] = Decimal(MemoryBytes), ["deadline_ms"] = Decimal(DeadlineMilliseconds), ["output_bytes"] = Decimal(OutputBytes),
        }], 8192, 1);
    }

    private void Validate()
    {
        if (Operation is not ("evaluate" or "simplify" or "differentiate" or "solve") ||
            ((Operation is "differentiate" or "solve") != (Variable is not null))) throw new FogException("invalid-request");
        if (Variable is not null && (Variable.Length is < 1 or > 64 || !char.IsAsciiLetter(Variable[0]) ||
            Variable.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))) throw new FogException("invalid-request");
    }

    private static int Number(IReadOnlyDictionary<string, string?> row, string field, int minimum, int maximum)
    {
        string? text = row[field];
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum || Decimal(value) != text)
            throw new FogException("invalid-request");
        return value;
    }

    internal static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed record FogMathOutcome(byte[] Result, string? Error, long CpuMilliseconds);

public interface IFogMathRunner
{
    Task<FogMathOutcome> RunAsync(FogMathJob job, byte[] input, CancellationToken cancellation);
}
