namespace SoftPilot.Application.Abstractions;

public interface IExternalRuntimeDetector
{
    RuntimeKind Kind { get; }
    Task<IReadOnlyList<ExternalRuntime>> DetectAsync(CancellationToken cancellationToken = default);
}
