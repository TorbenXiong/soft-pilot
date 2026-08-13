using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Infrastructure.Diagnostics;

public sealed class DoctorService : IDoctorService
{
    private readonly IInstallationLayout _layout;
    private readonly IRootRegistry _rootRegistry;
    private readonly IStateStore _stateStore;
    private readonly IShellIntegrationService _shell;
    private readonly ProcessRunner _processRunner;

    public DoctorService(
        IInstallationLayout layout,
        IRootRegistry rootRegistry,
        IStateStore stateStore,
        IShellIntegrationService shell,
        ProcessRunner processRunner)
    {
        _layout = layout;
        _rootRegistry = rootRegistry;
        _stateStore = stateStore;
        _shell = shell;
        _processRunner = processRunner;
    }

    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>();
        var os = Environment.OSVersion.Version;
        checks.Add(new DoctorCheck(
            "Windows 11 24H2+",
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100),
            $"当前版本 {os}"));

        var registeredRoot = _rootRegistry.ReadRoot();
        checks.Add(new DoctorCheck(
            "Root registry",
            registeredRoot is not null && string.Equals(registeredRoot, _layout.Root, StringComparison.OrdinalIgnoreCase),
            registeredRoot ?? "HKCU 未记录 SoftPilot Root"));

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_layout.Root)!);
            var passed = drive.DriveType == DriveType.Fixed && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
            checks.Add(new DoctorCheck("Workspace volume", passed, $"{drive.DriveType}, {drive.DriveFormat}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            checks.Add(new DoctorCheck("Workspace volume", false, exception.Message));
        }

        try
        {
            await _stateStore.InitializeAsync(cancellationToken);
            checks.Add(new DoctorCheck("SQLite state", true, "数据库可读写"));
        }
        catch (Exception exception)
        {
            checks.Add(new DoctorCheck("SQLite state", false, exception.Message));
        }

        var installations = await _stateStore.GetInstallationsAsync(cancellationToken: cancellationToken);
        foreach (var current in installations.Where(installation => installation.IsCurrent))
        {
            var link = _layout.GetCurrentLink(current.Kind);
            checks.Add(new DoctorCheck(
                $"Current {current.Kind}",
                Directory.Exists(link),
                Directory.Exists(link) ? link : "当前链接缺失"));
        }

        var shell = await _shell.GetStatusAsync(cancellationToken);
        checks.Add(new DoctorCheck(
            "Shell integration",
            !shell.IsEnabled || shell.IsShimPathFirst && shell.Problem is null,
            shell.IsEnabled ? shell.Problem ?? "已启用" : "未启用（可选）"));

        var currentNode = installations.FirstOrDefault(installation =>
            installation.Kind == RuntimeKind.Node && installation.IsCurrent);
        if (shell.IsEnabled && currentNode is not null)
        {
            foreach (var command in new[] { "node", "npm", "npx" })
            {
                checks.Add(await CheckCommandResolutionAsync(command, _layout.ShimsDirectory, cancellationToken));
            }

            checks.Add(await CheckNodeVersionAsync(currentNode, cancellationToken));
            checks.Add(await CheckNpmPrefixAsync(currentNode, cancellationToken));

            var corepack = new[] { "corepack.exe", "corepack.cmd" }
                .Select(fileName => Path.Combine(_layout.GetCurrentLink(RuntimeKind.Node), fileName))
                .FirstOrDefault(File.Exists);
            if (corepack is not null)
            {
                checks.Add(await CheckCommandResolutionAsync(
                    "corepack",
                    _layout.GetCurrentLink(RuntimeKind.Node),
                    cancellationToken));
            }
        }

        var staleStaging = Directory.Exists(_layout.StagingDirectory)
            ? Directory.EnumerateFileSystemEntries(_layout.StagingDirectory).Count()
            : 0;
        checks.Add(new DoctorCheck("Staging", staleStaging == 0, staleStaging == 0 ? "干净" : $"存在 {staleStaging} 个暂存项"));
        return checks;
    }

    private async Task<DoctorCheck> CheckCommandResolutionAsync(
        string command,
        string expectedDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync("where.exe", [command], cancellationToken: cancellationToken);
            var first = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            var passed = result.ExitCode == 0
                && first is not null
                && string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(first)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDirectory)),
                    StringComparison.OrdinalIgnoreCase);
            return new DoctorCheck(
                $"Command {command}",
                passed,
                first ?? "当前进程 PATH 中未找到；如果刚启用 Shell 集成，请打开新终端后重试");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DoctorCheck($"Command {command}", false, exception.Message);
        }
    }

    private async Task<DoctorCheck> CheckNodeVersionAsync(
        RuntimeInstallation currentNode,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync("node.exe", ["--version"], cancellationToken: cancellationToken);
            var actual = result.StandardOutput.Trim();
            var passed = result.ExitCode == 0 && RuntimeVersionMatcher.AreEquivalent(currentNode.Version, actual);
            return new DoctorCheck(
                "Node effective version",
                passed,
                passed ? actual : $"期望 {currentNode.Version}，实际 {(actual.Length == 0 ? "-" : actual)}；请检查 PATH 冲突或重开终端");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new DoctorCheck("Node effective version", false, exception.Message);
        }
    }

    private async Task<DoctorCheck> CheckNpmPrefixAsync(
        RuntimeInstallation currentNode,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync("npm.exe", ["prefix", "-g"], cancellationToken: cancellationToken);
            var prefix = result.StandardOutput.Trim();
            var passed = result.ExitCode == 0
                && (PathsEqual(prefix, _layout.GetCurrentLink(RuntimeKind.Node))
                    || PathsEqual(prefix, currentNode.InstallPath));
            return new DoctorCheck(
                "npm global prefix",
                passed,
                prefix.Length == 0 ? result.CombinedOutput : prefix);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new DoctorCheck("npm global prefix", false, exception.Message);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }
}
