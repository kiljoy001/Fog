// Specification-only API proposal. Not part of a production project or published SDK.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NinePSharp.Workloads.Proposed;

/// <summary>Author-supplied workload registration and per-job execution factory.</summary>
public interface IWorkloadProvider
{
    WorkloadDescriptor Describe();

    ValueTask<IWorkloadExecution> PrepareAsync(
        IWorkloadContext context,
        CancellationToken cancellationToken);
}

/// <summary>Stable LibTab documents; the host validates and freezes their content.</summary>
public sealed record WorkloadDescriptor(string ManifestText, string OptionsText);

/// <summary>One job's execution; stopping must be possible while RunAsync is pending.</summary>
public interface IWorkloadExecution : IAsyncDisposable
{
    ValueTask<WorkloadCompletion> RunAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>A candidate success, subject to host limits, cancellation, and cleanup.</summary>
public sealed record WorkloadCompletion(string FinishReason);

/// <summary>Worker-local capabilities; never an Orleans-serialized grain argument.</summary>
public interface IWorkloadContext
{
    string JobId { get; }

    IReadOnlyDictionary<string, string?> Specification { get; }

    Stream Input { get; }

    Stream Output { get; }

    IWorkloadArtifacts Artifacts { get; }

    IWorkloadNamespace Namespace { get; }

    IWorkloadBudget Budget { get; }
}

/// <summary>Only the immutable artifacts already resolved and pinned for this job.</summary>
public interface IWorkloadArtifacts
{
    ValueTask<Stream> OpenReadAsync(string specificationField, CancellationToken cancellationToken);
}

/// <summary>Authorized namespace IO; absence of a capability means access is denied.</summary>
public interface IWorkloadNamespace
{
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Host-owned limits and cumulative work meters; no reset or credit API.</summary>
public interface IWorkloadBudget
{
    TimeSpan RemainingTime { get; }

    ulong GetLimit(string field);

    void Charge(string meter, ulong amount);
}
