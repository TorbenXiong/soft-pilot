using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Infrastructure.Runtime;

public sealed class RedisServiceManager : IRedisServiceManager
{
    private const int Port = 6379;
    private const string RuntimeConfigName = "softpilot-redis.conf";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IInstallationLayout _layout;
    private readonly IStateStore _stateStore;
    private readonly RedisRuntimeProvider _provider;
    private readonly ProcessRunner _processRunner;
    private readonly WindowsTcpListenerProcessResolver _listenerProcessResolver;
    private readonly WorkspaceOperationLock _workspaceLock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;

    public RedisServiceManager(
        IInstallationLayout layout,
        IStateStore stateStore,
        RedisRuntimeProvider provider,
        ProcessRunner processRunner,
        WindowsTcpListenerProcessResolver listenerProcessResolver)
    {
        _layout = layout;
        _stateStore = stateStore;
        _provider = provider;
        _processRunner = processRunner;
        _listenerProcessResolver = listenerProcessResolver;
        _workspaceLock = new WorkspaceOperationLock(layout);
        _statePath = Path.Combine(layout.DataDirectory, "redis", "service-state.json");
    }

    public async Task<RedisServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetStatusWithinGateAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RedisServiceStatus> StartAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetStatusWithinGateAsync(cancellationToken);
            if (existing.IsRunning && string.Equals(existing.Version, version, StringComparison.Ordinal))
            {
                return existing;
            }

            if (existing.IsRunning)
            {
                await StopWithinGateAsync(cancellationToken);
            }
            else if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }

            var installation = await _stateStore.FindInstallationAsync(
                    RuntimeKind.Redis,
                    version,
                    cancellationToken: cancellationToken)
                ?? throw new RuntimeNotFoundException(RuntimeKind.Redis, version);
            var health = await _provider.CheckHealthAsync(installation.InstallPath, cancellationToken);
            if (!health.IsHealthy || !RuntimeVersionMatcher.AreEquivalent(version, health.DetectedVersion))
            {
                throw new SoftPilotException(
                    $"Redis {version} 启动前健康检查失败：{health.Error ?? $"实际版本 {health.DetectedVersion}"}");
            }

            var dataPath = _layout.GetRedisDataDirectory(version);
            var logPath = _layout.GetRedisLogPath(version);
            var configPath = Path.Combine(dataPath, "redis.conf");
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            if (!File.Exists(configPath))
            {
                await File.WriteAllTextAsync(
                    configPath,
                    BuildDefaultConfig(dataPath, logPath),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken);
            }

            var runtimeConfigPath = Path.Combine(installation.InstallPath, RuntimeConfigName);
            File.Copy(configPath, runtimeConfigPath, overwrite: true);
            var executablePath = Path.Combine(installation.InstallPath, "redis-server.exe");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = installation.InstallPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add(RuntimeConfigName);
            if (!process.Start())
            {
                throw new SoftPilotException($"无法启动 Redis {version}。");
            }

            RedisProcessState? state = null;
            try
            {
                state = new RedisProcessState(
                    version,
                    process.Id,
                    executablePath,
                    new DateTimeOffset(process.StartTime.ToUniversalTime()),
                    configPath,
                    dataPath,
                    logPath);
                await WriteStateAsync(state, cancellationToken);
                await WaitUntilReadyAsync(state, process, cancellationToken);
            }
            catch
            {
                var stopped = state is null
                    ? TryStopStartedProcess(process)
                    : TryKillOwnedProcess(state);
                if (stopped)
                {
                    DeleteState();
                }
                throw;
            }

            return ToStatus(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopWithinGateAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RedisServiceStatus> GetStatusWithinGateAsync(CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(cancellationToken);
        if (state is null)
        {
            return new RedisServiceStatus(false);
        }

        return IsOwnedProcessRunning(state)
            ? ToStatus(state)
            : ToStatus(state) with
            {
                IsRunning = false,
                ProcessId = null,
                Problem = "Redis 进程已退出，存在待清理的服务状态记录。",
            };
    }

    private async Task StopWithinGateAsync(CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(cancellationToken);
        if (state is null)
        {
            return;
        }

        if (!IsOwnedProcessRunning(state))
        {
            DeleteState();
            return;
        }

        var client = Path.Combine(Path.GetDirectoryName(state.ExecutablePath)!, "redis-cli.exe");
        if (File.Exists(client) && IsListenerOwnedBy(state.ProcessId))
        {
            using var gracefulTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gracefulTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _processRunner.RunAsync(
                    client,
                    ["-h", "127.0.0.1", "-p", Port.ToString(), "SHUTDOWN"],
                    cancellationToken: gracefulTimeout.Token);
            }
            catch (Exception exception) when (
                exception is SoftPilotException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // A customized password or a hung server can reject graceful shutdown.
            }
        }

        for (var attempt = 0; attempt < 20 && IsOwnedProcessRunning(state); attempt++)
        {
            await Task.Delay(100, cancellationToken);
        }

        if (IsOwnedProcessRunning(state))
        {
            if (!TryKillOwnedProcess(state))
            {
                throw new SoftPilotException(
                    $"无法终止已验证的 Redis 进程 {state.ProcessId}；服务状态已保留，请检查权限后重试。");
            }
        }

        DeleteState();
    }

    private async Task WaitUntilReadyAsync(
        RedisProcessState state,
        Process process,
        CancellationToken cancellationToken)
    {
        var client = Path.Combine(Path.GetDirectoryName(state.ExecutablePath)!, "redis-cli.exe");
        string? lastError = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                break;
            }

            try
            {
                var result = await _processRunner.RunAsync(
                    client,
                    ["-h", "127.0.0.1", "-p", Port.ToString(), "PING"],
                    cancellationToken: cancellationToken);
                if (result.ExitCode == 0
                    && string.Equals(result.StandardOutput.Trim(), "PONG", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsOwnedProcessRunning(state))
                    {
                        lastError = "Redis 启动进程的 PID、可执行文件路径或启动时间不再匹配。";
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    if (!IsListenerOwnedBy(state.ProcessId))
                    {
                        lastError = $"端口 {Port} 的 Windows 监听进程不是刚启动的 Redis 进程。";
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    var info = await _processRunner.RunAsync(
                        client,
                        ["-h", "127.0.0.1", "-p", Port.ToString(), "--raw", "INFO", "server"],
                        cancellationToken: cancellationToken);
                    if (info.ExitCode == 0 && IsExpectedServerInfo(info.StandardOutput, state.Version))
                    {
                        return;
                    }

                    lastError = "端口上的 Redis 版本与待启动版本不匹配。";
                    await Task.Delay(200, cancellationToken);
                    continue;
                }

                lastError = result.CombinedOutput;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or SoftPilotException)
            {
                lastError = exception.Message;
            }

            await Task.Delay(200, cancellationToken);
        }

        var logHint = File.Exists(state.LogPath)
            ? $" 请检查日志：{state.LogPath}"
            : string.Empty;
        throw new SoftPilotException(
            $"Redis {state.Version} 未能在端口 {Port} 上通过 PING 健康检查。{lastError}{logHint}");
    }

    private async Task<RedisProcessState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<RedisProcessState>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                ?? throw new JsonException("服务状态为空。");
        }
        catch (JsonException exception)
        {
            throw new SoftPilotException("Redis 服务状态文件损坏；为避免终止无关进程，已停止操作。", exception);
        }
    }

    private async Task WriteStateAsync(RedisProcessState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsOwnedProcessRunning(RedisProcessState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            return IsOwnedProcess(state, process);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private bool IsListenerOwnedBy(int processId)
    {
        try
        {
            return _listenerProcessResolver.GetListenerProcessIds(Port).Contains(processId);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryKillOwnedProcess(RedisProcessState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            if (!IsOwnedProcess(state, process))
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The verified process may exit between validation and termination.
        }

        return !IsOwnedProcessRunning(state);
    }

    private static bool IsOwnedProcess(RedisProcessState state, Process process)
    {
        if (process.HasExited || process.Id != state.ProcessId)
        {
            return false;
        }

        var actualPath = process.MainModule?.FileName;
        var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
        return string.Equals(
                Path.GetFullPath(actualPath ?? string.Empty),
                Path.GetFullPath(state.ExecutablePath),
                StringComparison.OrdinalIgnoreCase)
            && (actualStart - state.StartedAt).Duration() < TimeSpan.FromSeconds(2);
    }

    private static bool TryStopStartedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }

            return process.HasExited;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void DeleteState()
    {
        if (File.Exists(_statePath))
        {
            File.Delete(_statePath);
        }
    }

    private static RedisServiceStatus ToStatus(RedisProcessState state) => new(
        true,
        state.Version,
        state.ProcessId,
        state.StartedAt,
        state.ConfigPath,
        state.DataPath,
        state.LogPath);

    internal static string BuildDefaultConfig(string dataPath, string logPath) =>
        $"""
        bind 127.0.0.1
        protected-mode yes
        port {Port}
        daemonize no
        dir "{ToRedisPath(dataPath)}"
        logfile "{ToRedisPath(logPath)}"
        dbfilename dump.rdb
        appendonly no
        """ + Environment.NewLine;

    internal static bool IsExpectedServerInfo(string info, string version)
    {
        var values = info
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        return values.TryGetValue("redis_version", out var actualVersion)
            && RuntimeVersionMatcher.AreEquivalent(version, actualVersion);
    }

    private static string ToRedisPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record RedisProcessState(
        string Version,
        int ProcessId,
        string ExecutablePath,
        DateTimeOffset StartedAt,
        string ConfigPath,
        string DataPath,
        string LogPath);
}
