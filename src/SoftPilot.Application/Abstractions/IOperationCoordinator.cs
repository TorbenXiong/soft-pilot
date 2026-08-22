namespace SoftPilot.Application.Abstractions;

public interface IOperationCoordinator
{
    Task InstallAsync(RuntimeTarget target, bool makeCurrent, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task UpgradeAsync(RuntimeTarget target, bool makeCurrent, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task UninstallAsync(
        RuntimeTarget target,
        RuntimeUninstallOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeUninstallOptions(bool DeleteData = false);
