using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;

namespace SoftPilot.Tests;

internal sealed class InMemoryStateStore : IStateStore
{
    private readonly List<RuntimeInstallation> _installations = [];
    private readonly List<OperationRecord> _operations = [];

    public Func<RuntimeKind, string?, Exception?>? SetCurrentFailure { get; set; }

    public Action<RuntimeInstallation>? AfterUpsertInstallation { get; set; }

    public Action<RuntimeKind, string>? BeforeDeleteInstallation { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<RuntimeInstallation>> GetInstallationsAsync(
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RuntimeInstallation> result = _installations
            .Where(item => includeDeleted || !item.IsDeleted)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<RuntimeInstallation?> FindInstallationAsync(
        RuntimeKind kind,
        string version,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _installations.FirstOrDefault(item =>
            item.Kind == kind
            && string.Equals(item.Version, version, StringComparison.Ordinal)
            && (includeDeleted || !item.IsDeleted));
        return Task.FromResult(result);
    }

    public Task UpsertInstallationAsync(
        RuntimeInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _installations.RemoveAll(item => item.Kind == installation.Kind && item.Version == installation.Version);
        _installations.Add(installation);
        AfterUpsertInstallation?.Invoke(installation);
        return Task.CompletedTask;
    }

    public Task SetCurrentAsync(
        RuntimeKind kind,
        string? version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SetCurrentFailure?.Invoke(kind, version) is { } exception)
        {
            throw exception;
        }

        if (version is not null && !_installations.Any(item =>
                item.Kind == kind && item.Version == version && !item.IsDeleted))
        {
            throw new InvalidOperationException($"Missing installation {kind}@{version}.");
        }

        for (var index = 0; index < _installations.Count; index++)
        {
            var item = _installations[index];
            if (item.Kind == kind)
            {
                _installations[index] = item with { IsCurrent = item.Version == version };
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkDeletedAsync(
        RuntimeKind kind,
        string version,
        DateTimeOffset deletedAt,
        string trashPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateInstallation(kind, version, item => item with
        {
            IsCurrent = false,
            DeletedAt = deletedAt,
            TrashPath = trashPath,
        });
        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        RuntimeKind kind,
        string version,
        string installPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateInstallation(kind, version, item => item with
        {
            InstallPath = installPath,
            DeletedAt = null,
            TrashPath = null,
        });
        return Task.CompletedTask;
    }

    public Task DeleteInstallationAsync(
        RuntimeKind kind,
        string version,
        CancellationToken cancellationToken = default)
    {
        BeforeDeleteInstallation?.Invoke(kind, version);
        cancellationToken.ThrowIfCancellationRequested();
        _installations.RemoveAll(item => item.Kind == kind && item.Version == version);
        return Task.CompletedTask;
    }

    public Task AddOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operations.Add(operation);
        return Task.CompletedTask;
    }

    public Task CompleteOperationAsync(
        Guid id,
        OperationStatus status,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _operations.FindIndex(item => item.Id == id);
        if (index >= 0)
        {
            _operations[index] = _operations[index] with
            {
                Status = status,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = error,
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OperationRecord>> GetOperationsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OperationRecord> result = _operations.TakeLast(limit).Reverse().ToArray();
        return Task.FromResult(result);
    }

    public Task<OperationRecord?> FindOperationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_operations.FirstOrDefault(item => item.Id == id));
    }

    private void UpdateInstallation(
        RuntimeKind kind,
        string version,
        Func<RuntimeInstallation, RuntimeInstallation> update)
    {
        var index = _installations.FindIndex(item => item.Kind == kind && item.Version == version);
        if (index < 0)
        {
            throw new InvalidOperationException($"Missing installation {kind}@{version}.");
        }

        _installations[index] = update(_installations[index]);
    }
}
