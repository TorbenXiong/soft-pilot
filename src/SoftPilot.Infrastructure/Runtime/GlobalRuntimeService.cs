namespace SoftPilot.Infrastructure.Runtime;

public sealed class GlobalRuntimeService : IGlobalRuntimeService
{
    private readonly IStateStore _stateStore;
    private readonly IInstallationLayout _layout;
    private readonly WindowsDirectoryLinkService _links;
    private readonly IReadOnlyDictionary<RuntimeKind, IRuntimeProvider> _providers;
    private readonly WorkspaceOperationLock _workspaceLock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GlobalRuntimeService(
        IStateStore stateStore,
        IInstallationLayout layout,
        WindowsDirectoryLinkService links,
        IEnumerable<IRuntimeProvider> providers)
    {
        _stateStore = stateStore;
        _layout = layout;
        _links = links;
        _providers = providers.ToDictionary(provider => provider.Kind);
        _workspaceLock = new WorkspaceOperationLock(layout);
    }

    public async Task UseAsync(RuntimeKind kind, string version, CancellationToken cancellationToken = default)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await UseWithinWorkspaceLockAsync(kind, version, cancellationToken);
    }

    internal async Task UseWithinWorkspaceLockAsync(
        RuntimeKind kind,
        string version,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var installation = await _stateStore.FindInstallationAsync(kind, version, cancellationToken: cancellationToken)
                ?? throw new RuntimeNotFoundException(kind, version);
            if (!Directory.Exists(installation.InstallPath))
            {
                throw new SoftPilotException($"运行时目录不存在：{installation.InstallPath}");
            }

            var previous = (await GetCurrentAsync(cancellationToken))[kind];
            var link = _layout.GetCurrentLink(kind);
            await _links.ReplaceAsync(link, installation.InstallPath, cancellationToken);
            try
            {
                if (!_providers.TryGetValue(kind, out var provider))
                {
                    throw new SoftPilotException($"没有注册 {kind} Provider。");
                }

                var health = await provider.CheckHealthAsync(link, cancellationToken);
                if (!health.IsHealthy)
                {
                    throw new SoftPilotException($"切换后的运行时健康检查失败：{health.Error}");
                }

                if (!RuntimeVersionMatcher.AreEquivalent(version, health.DetectedVersion))
                {
                    throw new IntegrityException(
                        $"切换目标版本 {version} 与实际版本 {health.DetectedVersion} 不一致。");
                }

                await _stateStore.SetCurrentAsync(kind, version, cancellationToken);
            }
            catch
            {
                if (previous is not null)
                {
                    await _links.ReplaceAsync(link, previous.InstallPath, cancellationToken);
                }
                else
                {
                    _links.Delete(link);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(RuntimeKind kind, CancellationToken cancellationToken = default)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _links.Delete(_layout.GetCurrentLink(kind));
            await _stateStore.SetCurrentAsync(kind, null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<RuntimeKind, RuntimeInstallation?>> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        var installations = await _stateStore.GetInstallationsAsync(cancellationToken: cancellationToken);
        return Enum.GetValues<RuntimeKind>().ToDictionary(
            kind => kind,
            kind => installations.FirstOrDefault(installation => installation.Kind == kind && installation.IsCurrent));
    }
}
