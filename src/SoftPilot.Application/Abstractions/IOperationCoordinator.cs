namespace SoftPilot.Application.Abstractions;

public interface IOperationCoordinator
{
    Task InstallAsync(RuntimeTarget target, bool makeCurrent, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task UninstallAsync(RuntimeTarget target, CancellationToken cancellationToken = default);
}
