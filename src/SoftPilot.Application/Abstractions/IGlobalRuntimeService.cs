namespace SoftPilot.Application.Abstractions;

public interface IGlobalRuntimeService
{
    Task UseAsync(RuntimeKind kind, string version, CancellationToken cancellationToken = default);
    Task ClearAsync(RuntimeKind kind, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<RuntimeKind, RuntimeInstallation?>> GetCurrentAsync(CancellationToken cancellationToken = default);
}
