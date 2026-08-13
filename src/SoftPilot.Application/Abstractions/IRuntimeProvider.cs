namespace SoftPilot.Application.Abstractions;

public interface IRuntimeProvider
{
    RuntimeKind Kind { get; }

    Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(CancellationToken cancellationToken = default);
    Task<RuntimeRelease> ResolveAsync(string exactVersion, CancellationToken cancellationToken = default);
    Task PrepareAsync(
        RuntimeRelease release,
        string stagingDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<RuntimeHealth> CheckHealthAsync(string runtimeDirectory, CancellationToken cancellationToken = default);
}

public sealed record OperationProgress(string Stage, double? Percentage = null, string? Detail = null);

public sealed record RuntimeHealth(bool IsHealthy, string? DetectedVersion, string? Error = null);
