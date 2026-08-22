namespace SoftPilot.Infrastructure.Runtime;

public sealed class GlobalRuntimeService : IGlobalRuntimeService
{
    private readonly IStateStore _stateStore;
    private readonly IInstallationLayout _layout;
    private readonly WindowsDirectoryLinkService _links;
    private readonly IReadOnlyDictionary<RuntimeKind, IRuntimeProvider> _providers;
    private readonly IShellIntegrationService _shell;
    private readonly WorkspaceOperationLock _workspaceLock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GlobalRuntimeService(
        IStateStore stateStore,
        IInstallationLayout layout,
        WindowsDirectoryLinkService links,
        IEnumerable<IRuntimeProvider> providers,
        IShellIntegrationService shell)
    {
        _stateStore = stateStore;
        _layout = layout;
        _links = links;
        _providers = providers.ToDictionary(provider => provider.Kind);
        _shell = shell;
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
            var shellWasEnabled = (await _shell.GetStatusAsync(cancellationToken)).IsEnabled;
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

                if (!RuntimeVersionMatcher.AreEquivalent(kind, version, health.DetectedVersion))
                {
                    throw new IntegrityException(
                        $"切换目标版本 {version} 与实际版本 {health.DetectedVersion} 不一致。");
                }

                await _stateStore.SetCurrentAsync(kind, version, cancellationToken);
            }
            catch (Exception operationException)
            {
                try
                {
                    if (previous is not null)
                    {
                        await _links.ReplaceAsync(link, previous.InstallPath, CancellationToken.None);
                    }
                    else
                    {
                        _links.Delete(link);
                    }
                }
                catch (Exception rollbackException)
                {
                    throw CreateRollbackException("切换全局运行时", operationException, rollbackException);
                }

                throw;
            }

            try
            {
                await _shell.EnableAsync(cancellationToken);
            }
            catch (Exception operationException)
            {
                await RollbackSelectionAsync(
                    kind,
                    previous,
                    link,
                    operationException,
                    restoreShell: shellWasEnabled);
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
            var current = await GetCurrentAsync(cancellationToken);
            var previous = current[kind];
            var link = _layout.GetCurrentLink(kind);
            var hasOtherCurrentVersion = current
                .Where(pair => pair.Key != kind)
                .Any(pair => pair.Value is not null);
            var shellWasEnabled = (await _shell.GetStatusAsync(cancellationToken)).IsEnabled;
            _links.Delete(link);
            try
            {
                await _stateStore.SetCurrentAsync(kind, null, cancellationToken);
            }
            catch (Exception operationException)
            {
                if (previous is not null)
                {
                    try
                    {
                        await _links.ReplaceAsync(link, previous.InstallPath, CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        throw CreateRollbackException("清除全局运行时", operationException, rollbackException);
                    }
                }

                throw;
            }

            if (previous is not null)
            {
                try
                {
                    if (hasOtherCurrentVersion)
                    {
                        await _shell.EnableAsync(cancellationToken);
                    }
                    else if (shellWasEnabled)
                    {
                        await _shell.DisableAsync(cancellationToken);
                    }
                }
                catch (Exception operationException)
                {
                    await RollbackSelectionAsync(
                        kind,
                        previous,
                        link,
                        operationException,
                        restoreShell: shellWasEnabled);
                    throw;
                }
            }
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

    public async Task ReconcileShellIntegrationAsync(CancellationToken cancellationToken = default)
    {
        var hasCurrentVersion = (await GetCurrentAsync(cancellationToken)).Values.Any(value => value is not null);
        var status = await _shell.GetStatusAsync(cancellationToken);
        if (hasCurrentVersion && (!status.IsEnabled || status.Problem is not null))
        {
            await _shell.EnableAsync(cancellationToken);
        }
        else if (!hasCurrentVersion && status.IsEnabled)
        {
            await _shell.DisableAsync(cancellationToken);
        }
    }

    private async Task RollbackSelectionAsync(
        RuntimeKind kind,
        RuntimeInstallation? previous,
        string link,
        Exception operationException,
        bool restoreShell)
    {
        var rollbackExceptions = new List<Exception>();
        try
        {
            if (previous is null)
            {
                _links.Delete(link);
            }
            else
            {
                await _links.ReplaceAsync(link, previous.InstallPath, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            rollbackExceptions.Add(exception);
        }

        try
        {
            await _stateStore.SetCurrentAsync(kind, previous?.Version, CancellationToken.None);
        }
        catch (Exception exception)
        {
            rollbackExceptions.Add(exception);
        }

        try
        {
            if (restoreShell)
            {
                await _shell.EnableAsync(CancellationToken.None);
            }
            else
            {
                await _shell.DisableAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            rollbackExceptions.Add(exception);
        }

        if (rollbackExceptions.Count > 0)
        {
            throw new GlobalRuntimeRollbackException(
                "自动更新终端环境失败，且未能完整恢复原状态。请运行 spt doctor 检查当前版本和环境变量。",
                new AggregateException([operationException, .. rollbackExceptions]));
        }
    }

    private static GlobalRuntimeRollbackException CreateRollbackException(
        string operation,
        Exception operationException,
        Exception rollbackException) =>
        new(
            $"{operation}失败，且未能恢复原状态。请运行 spt doctor 并检查 current 链接。",
            new AggregateException(operationException, rollbackException));
}

internal sealed class GlobalRuntimeRollbackException : SoftPilotException
{
    public GlobalRuntimeRollbackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
