namespace SoftPilot.Infrastructure.Runtime;

public sealed class OperationCoordinator : IOperationCoordinator
{
    private readonly IReadOnlyDictionary<RuntimeKind, IRuntimeProvider> _providers;
    private readonly IInstallationLayout _layout;
    private readonly IStateStore _stateStore;
    private readonly GlobalRuntimeService _globalRuntimeService;
    private readonly WorkspaceOperationLock _workspaceLock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OperationCoordinator(
        IEnumerable<IRuntimeProvider> providers,
        IInstallationLayout layout,
        IStateStore stateStore,
        GlobalRuntimeService globalRuntimeService)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
        _layout = layout;
        _stateStore = stateStore;
        _globalRuntimeService = globalRuntimeService;
        _workspaceLock = new WorkspaceOperationLock(layout);
    }

    public Task InstallAsync(
        RuntimeTarget target,
        bool makeCurrent,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        TrackAsync("install", target, cancellationToken, async operationToken =>
        {
            progress?.Report(new OperationProgress("prepare", 0, "正在准备安装"));
            ValidateVersion(target.Version);
            if (await _stateStore.FindInstallationAsync(target.Kind, target.Version, includeDeleted: true, operationToken) is not null)
            {
                throw new SoftPilotException($"{target} 已安装或仍在回收站中。");
            }

            if (!_providers.TryGetValue(target.Kind, out var provider))
            {
                throw new SoftPilotException($"没有注册 {target.Kind} Provider。");
            }

            progress?.Report(new OperationProgress("resolve", 5, "正在解析官方确定版本"));
            var release = await provider.ResolveAsync(target.Version, operationToken);
            var finalDirectory = _layout.GetRuntimeDirectory(target.Kind, release.Version);
            var stagingDirectory = Path.Combine(_layout.StagingDirectory, $"{target.Kind.ToString().ToLowerInvariant()}-{release.Version}-{Guid.NewGuid():N}");
            if (Directory.Exists(finalDirectory))
            {
                throw new SoftPilotException($"目标运行时目录已存在：{finalDirectory}");
            }

            try
            {
                progress?.Report(new OperationProgress("prepare", 10, "正在准备下载和暂存目录"));
                Directory.CreateDirectory(stagingDirectory);
                await provider.PrepareAsync(release, stagingDirectory, progress, operationToken);
                progress?.Report(new OperationProgress("health", 85, "正在执行版本和健康检查"));
                var health = await provider.CheckHealthAsync(stagingDirectory, operationToken);
                if (!health.IsHealthy)
                {
                    throw new SoftPilotException($"运行时健康检查失败：{health.Error}");
                }

                if (!RuntimeVersionMatcher.AreEquivalent(release.Version, health.DetectedVersion))
                {
                    throw new IntegrityException($"请求版本 {release.Version} 与实际版本 {health.DetectedVersion} 不一致。");
                }

                progress?.Report(new OperationProgress("commit", 92, "正在提交运行时目录"));
                Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
                await MoveDirectoryWithRetryAsync(stagingDirectory, finalDirectory, operationToken);
                var installation = new RuntimeInstallation(
                    target.Kind,
                    release.Version,
                    release.Architecture,
                    finalDirectory,
                    DateTimeOffset.UtcNow,
                    false);
                try
                {
                    progress?.Report(new OperationProgress("state", 96, "正在保存安装状态"));
                    await _stateStore.UpsertInstallationAsync(installation, operationToken);
                }
                catch
                {
                    await MoveDirectoryWithRetryAsync(finalDirectory, stagingDirectory, CancellationToken.None);
                    throw;
                }

                if (makeCurrent)
                {
                    try
                    {
                        progress?.Report(new OperationProgress("current", 98, "正在设为全局版本"));
                        await _globalRuntimeService.UseWithinWorkspaceLockAsync(target.Kind, release.Version, operationToken);
                    }
                    catch (GlobalRuntimeRollbackException)
                    {
                        // The current link may still reference this runtime. Preserve the installation
                        // so doctor and a later repair do not encounter a dangling link.
                        throw;
                    }
                    catch (Exception operationException)
                    {
                        try
                        {
                            await RollbackInstalledRuntimeAsync(installation, stagingDirectory);
                        }
                        catch (Exception rollbackException)
                        {
                            throw new SoftPilotException(
                                $"{target} 已安装但切换失败，且未能完整撤销安装。请运行 spt doctor 检查状态。",
                                new AggregateException(operationException, rollbackException));
                        }

                        throw;
                    }
                }

                progress?.Report(new OperationProgress("complete", 100, "安装完成"));
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    await DeleteDirectoryWithRetryAsync(stagingDirectory, CancellationToken.None);
                }

                TryDeleteEmptyRuntimeKindDirectory(finalDirectory);
            }
        });

    public Task UninstallAsync(RuntimeTarget target, CancellationToken cancellationToken = default) =>
        TrackAsync("uninstall", target, cancellationToken, async operationToken =>
        {
            var installation = await _stateStore.FindInstallationAsync(target.Kind, target.Version, cancellationToken: operationToken)
                ?? throw new RuntimeNotFoundException(target.Kind, target.Version);
            if (installation.IsCurrent)
            {
                throw new SoftPilotException("当前全局版本不能卸载；请先切换版本或取消当前选择。");
            }

            var removalDirectory = Path.Combine(
                _layout.StagingDirectory,
                $"uninstall-{target.Kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_layout.StagingDirectory);
            Directory.Move(installation.InstallPath, removalDirectory);
            try
            {
                await _stateStore.DeleteInstallationAsync(target.Kind, target.Version, operationToken);
                await DeleteDirectoryWithRetryAsync(removalDirectory, CancellationToken.None);
            }
            catch
            {
                if (Directory.Exists(removalDirectory) && !Directory.Exists(installation.InstallPath))
                {
                    Directory.Move(removalDirectory, installation.InstallPath);
                }

                if (await _stateStore.FindInstallationAsync(
                        target.Kind,
                        target.Version,
                        includeDeleted: true,
                        CancellationToken.None) is null)
                {
                    await _stateStore.UpsertInstallationAsync(installation, CancellationToken.None);
                }

                throw;
            }
        });

    private async Task TrackAsync(
        string name,
        RuntimeTarget target,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        var operation = new OperationRecord(
            Guid.NewGuid(),
            name,
            target.Kind,
            target.Version,
            OperationStatus.Running,
            DateTimeOffset.UtcNow);
        try
        {
            await _stateStore.AddOperationAsync(operation, cancellationToken);
            await action(cancellationToken);
            await _stateStore.CompleteOperationAsync(
                operation.Id,
                OperationStatus.Succeeded,
                cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await _stateStore.CompleteOperationAsync(operation.Id, OperationStatus.Cancelled, cancellationToken: CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await _stateStore.CompleteOperationAsync(operation.Id, OperationStatus.Failed, exception.Message, CancellationToken.None);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || version is "." or "..")
        {
            throw new ArgumentException("版本号不能安全地用作目录名。", nameof(version));
        }
    }

    private static async Task MoveDirectoryWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (
                attempt < 3 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (1 << attempt)), cancellationToken);
            }
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 3 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (1 << attempt)), cancellationToken);
            }
        }
    }

    private static void TryDeleteEmptyRuntimeKindDirectory(string runtimeDirectory)
    {
        var kindDirectory = Path.GetDirectoryName(runtimeDirectory);
        if (kindDirectory is not null
            && Directory.Exists(kindDirectory)
            && !Directory.EnumerateFileSystemEntries(kindDirectory).Any())
        {
            Directory.Delete(kindDirectory);
        }
    }

    private async Task RollbackInstalledRuntimeAsync(
        RuntimeInstallation installation,
        string stagingDirectory)
    {
        await MoveDirectoryWithRetryAsync(
            installation.InstallPath,
            stagingDirectory,
            CancellationToken.None);
        try
        {
            await _stateStore.DeleteInstallationAsync(
                installation.Kind,
                installation.Version,
                CancellationToken.None);
        }
        catch (Exception stateException)
        {
            try
            {
                await MoveDirectoryWithRetryAsync(
                    stagingDirectory,
                    installation.InstallPath,
                    CancellationToken.None);
            }
            catch (Exception directoryException)
            {
                throw new SoftPilotException(
                    $"撤销 {installation.Kind.ToString().ToLowerInvariant()}@{installation.Version} 的状态记录失败，且未能恢复运行时目录。",
                    new AggregateException(stateException, directoryException));
            }

            throw;
        }
    }
}
