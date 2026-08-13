namespace SoftPilot.Application.Abstractions;

public interface IStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeInstallation>> GetInstallationsAsync(
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);
    Task<RuntimeInstallation?> FindInstallationAsync(
        RuntimeKind kind,
        string version,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);
    Task UpsertInstallationAsync(RuntimeInstallation installation, CancellationToken cancellationToken = default);
    Task SetCurrentAsync(RuntimeKind kind, string? version, CancellationToken cancellationToken = default);
    Task MarkDeletedAsync(RuntimeKind kind, string version, DateTimeOffset deletedAt, string trashPath, CancellationToken cancellationToken = default);
    Task RestoreAsync(RuntimeKind kind, string version, string installPath, CancellationToken cancellationToken = default);
    Task DeleteInstallationAsync(RuntimeKind kind, string version, CancellationToken cancellationToken = default);
    Task AddOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default);
    Task CompleteOperationAsync(Guid id, OperationStatus status, string? error = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationRecord>> GetOperationsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<OperationRecord?> FindOperationAsync(Guid id, CancellationToken cancellationToken = default);
}
