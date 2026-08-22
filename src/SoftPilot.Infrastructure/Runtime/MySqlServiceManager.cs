using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Providers;
using SoftPilot.Infrastructure.Security;

namespace SoftPilot.Infrastructure.Runtime;

public sealed class MySqlServiceManager : IMySqlServiceManager
{
    private const int DefaultPort = 3306;
    private const string BootstrapMarkerContent = "root-loopback-password-configured\n";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IInstallationLayout _layout;
    private readonly IStateStore _stateStore;
    private readonly MySqlRuntimeProvider _provider;
    private readonly ProcessRunner _processRunner;
    private readonly WindowsTcpListenerProcessResolver _listenerProcessResolver;
    private readonly WorkspaceOperationLock _workspaceLock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MySqlServiceManager(
        IInstallationLayout layout,
        IStateStore stateStore,
        MySqlRuntimeProvider provider,
        ProcessRunner processRunner,
        WindowsTcpListenerProcessResolver listenerProcessResolver)
    {
        _layout = layout;
        _stateStore = stateStore;
        _provider = provider;
        _processRunner = processRunner;
        _listenerProcessResolver = listenerProcessResolver;
        _workspaceLock = new WorkspaceOperationLock(layout);
    }

    public async Task<IReadOnlyList<MySqlServiceStatus>> GetStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var installations = await _stateStore.GetInstallationsAsync(
                includeDeleted: false,
                cancellationToken: cancellationToken);
            var statuses = new List<MySqlServiceStatus>();
            foreach (var installation in installations.Where(item => item.Kind == RuntimeKind.MySql))
            {
                try
                {
                    statuses.Add(await GetStatusWithinGateAsync(installation.Version, cancellationToken));
                }
                catch (SoftPilotException exception)
                {
                    statuses.Add(new MySqlServiceStatus(
                        false,
                        installation.Version,
                        Problem: exception.Message,
                        Port: GetDefaultPort(installation.Version)));
                }
            }

            return statuses;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MySqlServiceStatus> GetStatusAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetStatusWithinGateAsync(version, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MySqlServiceStatus> StartAsync(string version, CancellationToken cancellationToken = default)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetStatusWithinGateAsync(version, cancellationToken);
            if (existing.IsRunning && existing.Problem is null)
            {
                return existing;
            }

            DeleteState(version);

            var installation = await _stateStore.FindInstallationAsync(
                    RuntimeKind.MySql,
                    version,
                    cancellationToken: cancellationToken)
                ?? throw new RuntimeNotFoundException(RuntimeKind.MySql, version);
            var health = await _provider.CheckHealthAsync(installation.InstallPath, cancellationToken);
            if (!health.IsHealthy || !RuntimeVersionMatcher.AreEquivalent(RuntimeKind.MySql, version, health.DetectedVersion))
            {
                throw new SoftPilotException(
                    $"MySQL {version} 启动前健康检查失败：{health.Error ?? $"实际版本 {health.DetectedVersion}"}");
            }

            var port = GetConfiguredPort(version);
            var listeners = _listenerProcessResolver.GetListenerProcessIds(port);
            if (listeners.Count > 0)
            {
                throw new SoftPilotException(
                    $"MySQL {version} 无法启动：端口 {port} 已被 PID {string.Join(", ", listeners.Order())} 占用。请修改该版本端口后重试。");
            }

            var paths = EnsurePaths(version, installation.InstallPath, port);
            await EnsureInitializedAsync(version, installation.InstallPath, paths, cancellationToken);
            var password = await ReadPasswordAsync(version, cancellationToken);
            var executablePath = Path.Combine(installation.InstallPath, "bin", "mysqld.exe");
            using var process = StartServer(executablePath, installation.InstallPath, paths.ConfigPath);
            var state = new MySqlProcessState(
                version,
                process.Id,
                executablePath,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                paths.ConfigPath,
                paths.DataPath,
                paths.LogPath,
                port);
            try
            {
                await WriteStateAsync(state, cancellationToken);
                await WaitUntilReadyAsync(state, process, password, cancellationToken);
            }
            catch
            {
                if (TryKillOwnedProcess(state))
                {
                    DeleteState(version);
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

    public async Task StopAsync(string version, CancellationToken cancellationToken = default)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopWithinGateAsync(version, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MySqlCredentials> GetCredentialsAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        var password = await ReadPasswordAsync(version, cancellationToken);
        return new MySqlCredentials("127.0.0.1", GetConfiguredPort(version), "root", password);
    }

    public int GetConfiguredPort(string version)
    {
        var path = GetPortPath(version);
        if (!File.Exists(path))
        {
            return GetDefaultPort(version);
        }

        var raw = File.ReadAllText(path).Trim();
        return int.TryParse(raw, out var port) && IsValidPort(port)
            ? port
            : throw new SoftPilotException($"MySQL {version} 端口配置无效：{raw}");
    }

    public async Task SetConfiguredPortAsync(
        string version,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidPort(port))
        {
            throw new SoftPilotException("MySQL 端口必须在 1 到 65535 之间。");
        }

        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = await _stateStore.FindInstallationAsync(
                    RuntimeKind.MySql,
                    version,
                    cancellationToken: cancellationToken)
                ?? throw new RuntimeNotFoundException(RuntimeKind.MySql, version);
            var installations = await _stateStore.GetInstallationsAsync(
                includeDeleted: false,
                cancellationToken: cancellationToken);
            var conflictingVersion = installations
                .Where(item => item.Kind == RuntimeKind.MySql
                    && !string.Equals(item.Version, version, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Version)
                .FirstOrDefault(otherVersion => GetConfiguredPort(otherVersion) == port);
            if (conflictingVersion is not null)
            {
                throw new SoftPilotException(
                    $"端口 {port} 已分配给 MySQL {conflictingVersion}；不同 MySQL 实例必须使用不同端口。");
            }

            var status = await GetStatusWithinGateAsync(version, cancellationToken);
            if (status.IsRunning)
            {
                throw new SoftPilotException($"MySQL {version} 正在运行；请先停止后再修改端口。");
            }

            var path = GetPortPath(version);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, port.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(
        string version,
        string runtimePath,
        MySqlPaths paths,
        CancellationToken cancellationToken)
    {
        var systemDirectory = Path.Combine(paths.DataPath, "mysql");
        var markerPath = GetBootstrapMarkerPath(version);
        if (!Directory.Exists(systemDirectory))
        {
            if (Directory.Exists(paths.DataPath) && Directory.EnumerateFileSystemEntries(paths.DataPath).Any())
            {
                throw new SoftPilotException($"MySQL 数据目录非空但未包含系统表，拒绝覆盖：{paths.DataPath}");
            }

            Directory.CreateDirectory(paths.DataPath);
            var server = Path.Combine(runtimePath, "bin", "mysqld.exe");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var initialize = await _processRunner.RunAsync(
                server,
                [$"--defaults-file={paths.ConfigPath}", "--initialize-insecure"],
                workingDirectory: runtimePath,
                cancellationToken: timeout.Token);
            if (initialize.ExitCode != 0)
            {
                throw new SoftPilotException($"MySQL {version} 初始化数据目录失败：{initialize.CombinedOutput}");
            }
        }

        if (File.Exists(markerPath)
            && string.Equals(
                await File.ReadAllTextAsync(markerPath, cancellationToken),
                BootstrapMarkerContent,
                StringComparison.Ordinal))
        {
            _ = await ReadPasswordAsync(version, cancellationToken);
            return;
        }

        var passwordPath = GetPasswordPath(version);
        string password;
        if (File.Exists(passwordPath))
        {
            password = await ReadPasswordAsync(version, cancellationToken);
        }
        else
        {
            password = GeneratePassword();
            await WritePasswordAsync(version, password, cancellationToken);
        }

        await BootstrapPasswordAsync(version, runtimePath, paths, password, cancellationToken);
        await File.WriteAllTextAsync(markerPath, BootstrapMarkerContent, cancellationToken);
    }

    private async Task BootstrapPasswordAsync(
        string version,
        string runtimePath,
        MySqlPaths paths,
        string password,
        CancellationToken cancellationToken)
    {
        var server = Path.Combine(runtimePath, "bin", "mysqld.exe");
        var sharedMemoryName = $"SoftPilotMySql-{GetReleaseLine(version).Replace('.', '-')}";
        var bootstrapArguments = BuildBootstrapServerArguments(sharedMemoryName);
        using var process = StartServer(
            server,
            runtimePath,
            paths.ConfigPath,
            bootstrapArguments);
        try
        {
            var client = Path.Combine(runtimePath, "bin", "mysql.exe");
            var ready = false;
            string? lastError = null;
            for (var attempt = 0; attempt < 60 && !process.HasExited; attempt++)
            {
                var probe = await RunBootstrapSqlAsync(client, sharedMemoryName, null, "SELECT VERSION();", cancellationToken);
                if (probe.ExitCode == 0)
                {
                    ready = true;
                    break;
                }

                var authenticated = await RunBootstrapSqlAsync(client, sharedMemoryName, password, "SELECT VERSION();", cancellationToken);
                if (authenticated.ExitCode == 0)
                {
                    ready = true;
                    break;
                }

                lastError = probe.CombinedOutput;
                await Task.Delay(200, cancellationToken);
            }

            if (!ready)
            {
                throw new SoftPilotException($"MySQL {version} 安全初始化通道未就绪：{lastError}");
            }

            var alreadyConfigured = await RunBootstrapSqlAsync(client, sharedMemoryName, password, "SELECT VERSION();", cancellationToken);
            if (alreadyConfigured.ExitCode != 0)
            {
                var alter = await RunBootstrapSqlAsync(
                    client,
                    sharedMemoryName,
                    null,
                    $"ALTER USER 'root'@'localhost' IDENTIFIED BY '{password}';",
                    cancellationToken);
                if (alter.ExitCode != 0)
                {
                    throw new SoftPilotException($"MySQL root 凭据初始化失败：{alter.CombinedOutput}");
                }
            }

            var configureLoopback = await RunBootstrapSqlAsync(
                client,
                sharedMemoryName,
                password,
                BuildLoopbackRootSql(password),
                cancellationToken);
            if (configureLoopback.ExitCode != 0)
            {
                throw new SoftPilotException(
                    $"MySQL root 回环 TCP 账户初始化失败：{configureLoopback.CombinedOutput}");
            }

            await StopBootstrapServerAsync(runtimePath, sharedMemoryName, password, cancellationToken);
            for (var attempt = 0; attempt < 50 && !process.HasExited; attempt++)
            {
                await Task.Delay(100, cancellationToken);
            }

            if (!process.HasExited)
            {
                throw new SoftPilotException("MySQL 安全初始化进程未能正常退出。");
            }
        }
        finally
        {
            TryStopStartedProcess(process);
        }
    }

    private async Task StopWithinGateAsync(string version, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(version, cancellationToken)
            ?? await FindUntrackedManagedProcessAsync(version, cancellationToken);
        if (state is null)
        {
            return;
        }

        if (!IsOwnedProcessRunning(state))
        {
            DeleteState(version);
            return;
        }

        if (IsListenerOwnedBy(state.Port, state.ProcessId))
        {
            try
            {
                var password = await ReadPasswordAsync(state.Version, cancellationToken);
                var admin = Path.Combine(Path.GetDirectoryName(state.ExecutablePath)!, "mysqladmin.exe");
                await WithClientDefaultsAsync(password, async defaultsPath =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    await _processRunner.RunAsync(
                        admin,
                        [$"--defaults-extra-file={defaultsPath}", "shutdown"],
                        cancellationToken: timeout.Token);
                }, port: state.Port);
            }
            catch (Exception exception) when (
                exception is SoftPilotException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Fallback remains constrained to the recorded PID, executable path and start time.
            }
        }

        for (var attempt = 0; attempt < 30 && IsOwnedProcessRunning(state); attempt++)
        {
            await Task.Delay(100, cancellationToken);
        }

        if (IsOwnedProcessRunning(state) && !TryKillOwnedProcess(state))
        {
            throw new SoftPilotException($"无法终止已验证的 MySQL 进程 {state.ProcessId}；服务状态已保留。");
        }

        DeleteState(version);
    }

    private async Task WaitUntilReadyAsync(
        MySqlProcessState state,
        Process process,
        string password,
        CancellationToken cancellationToken)
    {
        var client = Path.Combine(Path.GetDirectoryName(state.ExecutablePath)!, "mysql.exe");
        string? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                break;
            }

            try
            {
                var result = await WithClientDefaultsAsync(password, defaultsPath =>
                    _processRunner.RunAsync(
                        client,
                        [$"--defaults-extra-file={defaultsPath}", "--batch", "--skip-column-names"],
                        standardInput: "SELECT VERSION();\n",
                        cancellationToken: cancellationToken),
                    port: state.Port);
                var actual = result.StandardOutput.Trim();
                if (result.ExitCode == 0 && RuntimeVersionMatcher.AreEquivalent(RuntimeKind.MySql, state.Version, actual))
                {
                    if (IsOwnedProcessRunning(state) && IsListenerOwnedBy(state.Port, state.ProcessId))
                    {
                        return;
                    }

                    var listenerPids = _listenerProcessResolver.GetListenerProcessIds(state.Port);
                    lastError = listenerPids.Count == 0
                        ? $"端口 {state.Port} 尚未出现监听进程。"
                        : $"端口 {state.Port} 由 PID {string.Join(", ", listenerPids.Order())} 监听，而本次启动 PID 为 {state.ProcessId}。";
                }
                else
                {
                    lastError = result.CombinedOutput;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or SoftPilotException)
            {
                lastError = exception.Message;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new SoftPilotException(
            $"MySQL {state.Version} 未能在端口 {state.Port} 通过版本健康检查。{lastError} 请检查日志：{state.LogPath}");
    }

    private MySqlPaths EnsurePaths(string version, string runtimePath, int port)
    {
        var dataPath = _layout.GetMySqlDataDirectory(version);
        var logPath = _layout.GetMySqlLogPath(version);
        var configPath = _layout.GetMySqlConfigPath(version);
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(configPath, BuildDefaultConfig(runtimePath, dataPath, logPath, port), new UTF8Encoding(false));

        return new MySqlPaths(configPath, dataPath, logPath);
    }

    internal static string BuildDefaultConfig(string runtimePath, string dataPath, string logPath, int port = DefaultPort) =>
        $"""
        [mysqld]
        basedir="{ToMySqlPath(runtimePath)}"
        datadir="{ToMySqlPath(dataPath)}"
        bind-address=127.0.0.1
        port={port}
        skip-name-resolve
        character-set-server=utf8mb4
        log-error="{ToMySqlPath(logPath)}"

        [client]
        host=127.0.0.1
        port={port}
        protocol=tcp
        default-character-set=utf8mb4
        """ + Environment.NewLine;

    internal static string[] BuildBootstrapServerArguments(string sharedMemoryName) =>
    [
        "--skip-networking",
        "--shared-memory",
        $"--shared-memory-base-name={sharedMemoryName}",
    ];

    internal static string BuildLoopbackRootSql(string password) =>
        $"CREATE USER IF NOT EXISTS 'root'@'127.0.0.1' IDENTIFIED BY '{password}';" +
        $"ALTER USER 'root'@'127.0.0.1' IDENTIFIED BY '{password}';" +
        "GRANT ALL PRIVILEGES ON *.* TO 'root'@'127.0.0.1' WITH GRANT OPTION;";

    private static Process StartServer(
        string executable,
        string workingDirectory,
        string configPath,
        params string[] extraArguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add($"--defaults-file={configPath}");
        foreach (var argument in extraArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new SoftPilotException($"无法启动 MySQL：{executable}");
        }

        return process;
    }

    private async Task<ProcessResult> RunBootstrapSqlAsync(
        string client,
        string sharedMemoryName,
        string? password,
        string sql,
        CancellationToken cancellationToken)
    {
        if (password is null)
        {
            return await _processRunner.RunAsync(
                client,
                ["--protocol=MEMORY", $"--shared-memory-base-name={sharedMemoryName}", "--user=root", "--skip-password", "--batch", "--skip-column-names"],
                standardInput: sql + Environment.NewLine,
                cancellationToken: cancellationToken);
        }

        return await WithClientDefaultsAsync(password, defaultsPath =>
            _processRunner.RunAsync(
                client,
                [$"--defaults-extra-file={defaultsPath}", "--protocol=MEMORY", $"--shared-memory-base-name={sharedMemoryName}", "--batch", "--skip-column-names"],
                standardInput: sql + Environment.NewLine,
                cancellationToken: cancellationToken),
            includeNetworkSettings: false);
    }

    private async Task StopBootstrapServerAsync(
        string runtimePath,
        string sharedMemoryName,
        string password,
        CancellationToken cancellationToken)
    {
        var admin = Path.Combine(runtimePath, "bin", "mysqladmin.exe");
        await WithClientDefaultsAsync(password, defaultsPath =>
            _processRunner.RunAsync(
                admin,
                [$"--defaults-extra-file={defaultsPath}", "--protocol=MEMORY", $"--shared-memory-base-name={sharedMemoryName}", "shutdown"],
                cancellationToken: cancellationToken),
            includeNetworkSettings: false);
    }

    private async Task<T> WithClientDefaultsAsync<T>(
        string password,
        Func<string, Task<T>> action,
        bool includeNetworkSettings = true,
        int port = DefaultPort)
    {
        var directory = Path.Combine(_layout.DataDirectory, "mysql", ".client");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.ini");
        var network = includeNetworkSettings
            ? $"host=127.0.0.1{Environment.NewLine}port={port}{Environment.NewLine}protocol=tcp{Environment.NewLine}"
            : string.Empty;
        try
        {
            await File.WriteAllTextAsync(
                path,
                $"[client]{Environment.NewLine}user=root{Environment.NewLine}password={password}{Environment.NewLine}{network}",
                new UTF8Encoding(false));
            return await action(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private async Task WithClientDefaultsAsync(
        string password,
        Func<string, Task> action,
        bool includeNetworkSettings = true,
        int port = DefaultPort) =>
        await WithClientDefaultsAsync(password, async path =>
        {
            await action(path);
            return true;
        }, includeNetworkSettings, port);

    private async Task<string> ReadPasswordAsync(string version, CancellationToken cancellationToken)
    {
        var path = GetPasswordPath(version);
        if (!File.Exists(path))
        {
            throw new SoftPilotException($"MySQL {GetReleaseLine(version)} 尚未初始化凭据；请先启动服务。");
        }

        var protectedValue = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            return Encoding.UTF8.GetString(WindowsDataProtector.Unprotect(protectedValue));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or CryptographicException)
        {
            throw new SoftPilotException("MySQL 凭据无法由当前 Windows 用户解密。", exception);
        }
    }

    private async Task WritePasswordAsync(string version, string password, CancellationToken cancellationToken)
    {
        var path = GetPasswordPath(version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var protectedValue = WindowsDataProtector.Protect(Encoding.UTF8.GetBytes(password));
        await File.WriteAllBytesAsync(path, protectedValue, cancellationToken);
    }

    private string GetPasswordPath(string version) =>
        Path.Combine(Path.GetDirectoryName(_layout.GetMySqlDataDirectory(version))!, "credentials.bin");

    private string GetBootstrapMarkerPath(string version) =>
        Path.Combine(Path.GetDirectoryName(_layout.GetMySqlDataDirectory(version))!, "bootstrap.complete");

    private string GetPortPath(string version) =>
        Path.Combine(Path.GetDirectoryName(_layout.GetMySqlDataDirectory(version))!, "port.txt");

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    internal static int GetDefaultPort(string version) =>
        string.Equals(GetReleaseLine(version), "5.7", StringComparison.Ordinal) ? 3307 : DefaultPort;

    private async Task<MySqlServiceStatus> GetStatusWithinGateAsync(
        string version,
        CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(version, cancellationToken);
        if (state is null)
        {
            var untracked = await FindUntrackedManagedProcessAsync(version, cancellationToken);
            return untracked is null
                ? new MySqlServiceStatus(false, version, Port: GetConfiguredPort(version))
                : ToStatus(untracked) with
                {
                    Problem = $"检测到 MySQL {version} 受管进程，但缺少该版本的状态记录；请先停止该进程再卸载。",
                };
        }

        return IsOwnedProcessRunning(state)
            ? ToStatus(state)
            : ToStatus(state) with
            {
                IsRunning = false,
                ProcessId = null,
                Problem = "MySQL 进程已退出，存在待清理的服务状态记录。",
            };
    }

    private async Task<MySqlProcessState?> FindUntrackedManagedProcessAsync(
        string version,
        CancellationToken cancellationToken)
    {
        var installation = await _stateStore.FindInstallationAsync(
            RuntimeKind.MySql,
            version,
            cancellationToken: cancellationToken);
        if (installation is null)
        {
            return null;
        }

        var managedExecutable = Path.GetFullPath(Path.Combine(installation.InstallPath, "bin", "mysqld.exe"));

        foreach (var process in Process.GetProcessesByName("mysqld"))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.HasExited
                        || process.MainModule?.FileName is not { } actualPath
                        || !string.Equals(
                            Path.GetFullPath(actualPath),
                            managedExecutable,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return new MySqlProcessState(
                        installation.Version,
                        process.Id,
                        actualPath,
                        new DateTimeOffset(process.StartTime.ToUniversalTime()),
                        _layout.GetMySqlConfigPath(installation.Version),
                        _layout.GetMySqlDataDirectory(installation.Version),
                        _layout.GetMySqlLogPath(installation.Version),
                        GetConfiguredPort(installation.Version));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or UnauthorizedAccessException)
                {
                    // Ignore processes whose identity cannot be verified from their executable path.
                }
            }
        }

        return null;
    }

    private async Task<MySqlProcessState?> ReadStateAsync(
        string version,
        CancellationToken cancellationToken)
    {
        var statePath = GetStatePath(version);
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(statePath);
            var state = await JsonSerializer.DeserializeAsync<MySqlProcessState>(stream, JsonOptions, cancellationToken)
                ?? throw new JsonException("服务状态为空。");
            if (!string.Equals(GetReleaseLine(state.Version), GetReleaseLine(version), StringComparison.Ordinal))
            {
                throw new JsonException($"状态文件版本 {state.Version} 与目标版本 {version} 不一致。");
            }

            return IsValidPort(state.Port)
                ? state
                : state with { Port = GetConfiguredPort(state.Version) };
        }
        catch (JsonException exception)
        {
            throw new SoftPilotException("MySQL 服务状态文件损坏；为避免终止无关进程，已停止操作。", exception);
        }
    }

    private async Task WriteStateAsync(MySqlProcessState state, CancellationToken cancellationToken)
    {
        var statePath = GetStatePath(state.Version);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary = statePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private bool IsListenerOwnedBy(int port, int processId)
    {
        try
        {
            return _listenerProcessResolver.GetListenerProcessIds(port).Contains(processId);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsOwnedProcessRunning(MySqlProcessState state)
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

    private static bool TryKillOwnedProcess(MySqlProcessState state)
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

    private static bool IsOwnedProcess(MySqlProcessState state, Process process)
    {
        if (process.HasExited || process.Id != state.ProcessId)
        {
            return false;
        }

        var actualPath = process.MainModule?.FileName;
        var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
        return string.Equals(Path.GetFullPath(actualPath ?? string.Empty), Path.GetFullPath(state.ExecutablePath), StringComparison.OrdinalIgnoreCase)
            && (actualStart - state.StartedAt).Duration() < TimeSpan.FromSeconds(2);
    }

    private static void TryStopStartedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort only for a process created by this method.
        }
    }

    private string GetStatePath(string version) =>
        Path.Combine(Path.GetDirectoryName(_layout.GetMySqlDataDirectory(version))!, "service-state.json");

    private void DeleteState(string version)
    {
        var statePath = GetStatePath(version);
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }
    }

    private static MySqlServiceStatus ToStatus(MySqlProcessState state) => new(
        true,
        state.Version,
        state.ProcessId,
        state.StartedAt,
        state.ConfigPath,
        state.DataPath,
        state.LogPath,
        Port: state.Port);

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_";
        return string.Create(32, alphabet, static (span, chars) =>
        {
            span[0] = 'A';
            span[1] = 'a';
            span[2] = '2';
            span[3] = '-';
            for (var index = 4; index < span.Length; index++)
            {
                span[index] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }

            for (var index = span.Length - 1; index > 0; index--)
            {
                var swap = RandomNumberGenerator.GetInt32(index + 1);
                (span[index], span[swap]) = (span[swap], span[index]);
            }
        });
    }

    private static string GetReleaseLine(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }

    private static string ToMySqlPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private sealed record MySqlPaths(string ConfigPath, string DataPath, string LogPath);

    private sealed record MySqlProcessState(
        string Version,
        int ProcessId,
        string ExecutablePath,
        DateTimeOffset StartedAt,
        string ConfigPath,
        string DataPath,
        string LogPath,
        int Port);
}
