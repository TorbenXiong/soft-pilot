using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace SoftPilot.Infrastructure.Shell;

public sealed class WindowsShellIntegrationService : IShellIntegrationService
{
    private const uint WmSettingChange = 0x001A;
    private static readonly nint HwndBroadcast = new(0xffff);
    private readonly IInstallationLayout _layout;
    private readonly string _snapshotPath;

    public WindowsShellIntegrationService(IInstallationLayout layout)
    {
        _layout = layout;
        _snapshotPath = Path.Combine(layout.DataDirectory, "shell-environment.json");
    }

    public Task<ShellIntegrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var entries = SplitPath(path);
        var first = entries.FirstOrDefault();
        var nodeCurrent = _layout.GetCurrentLink(RuntimeKind.Node);
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User);
        var enabled = entries.Contains(_layout.ShimsDirectory, StringComparer.OrdinalIgnoreCase);
        var firstMatch = string.Equals(first, _layout.ShimsDirectory, StringComparison.OrdinalIgnoreCase);
        var nodePathPresent = entries.Contains(nodeCurrent, StringComparer.OrdinalIgnoreCase);
        var expectedJavaHome = _layout.GetCurrentLink(RuntimeKind.Java);
        var javaMatches = string.Equals(javaHome, expectedJavaHome, StringComparison.OrdinalIgnoreCase);
        var problems = new List<string>();
        if (enabled && !firstMatch)
        {
            problems.Add("SoftPilot shims 不在用户 PATH 首位");
        }

        if (enabled && !nodePathPresent)
        {
            problems.Add("Node.js 当前版本目录不在用户 PATH 中，全局 npm 命令将不可用");
        }

        if (enabled && !javaMatches)
        {
            problems.Add("JAVA_HOME 已被其他工具修改");
        }

        var problem = problems.Count == 0 ? null : string.Join("；", problems) + "。";
        return Task.FromResult(new ShellIntegrationStatus(enabled, firstMatch, javaHome, problem));
    }

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_layout.DataDirectory);
        if (!File.Exists(_snapshotPath))
        {
            var snapshot = new ShellEnvironmentSnapshot(
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User),
                DateTimeOffset.UtcNow);
            await WriteSnapshotAsync(snapshot, cancellationToken);
        }

        var existingPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var newPath = BuildEnabledPath(
            existingPath,
            _layout.ShimsDirectory,
            _layout.GetCurrentLink(RuntimeKind.Node));
        Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(
            "JAVA_HOME",
            _layout.GetCurrentLink(RuntimeKind.Java),
            EnvironmentVariableTarget.User);
        BroadcastEnvironmentChange();
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_snapshotPath))
        {
            var existingPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
            var cleaned = BuildDisabledPath(
                existingPath,
                _layout.ShimsDirectory,
                _layout.GetCurrentLink(RuntimeKind.Node));
            Environment.SetEnvironmentVariable("PATH", cleaned, EnvironmentVariableTarget.User);
            if (string.Equals(
                    Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User),
                    _layout.GetCurrentLink(RuntimeKind.Java),
                    StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("JAVA_HOME", null, EnvironmentVariableTarget.User);
            }

            BroadcastEnvironmentChange();
            return;
        }

        ShellEnvironmentSnapshot snapshot;
        await using (var stream = new FileStream(
            _snapshotPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            snapshot = await JsonSerializer.DeserializeAsync<ShellEnvironmentSnapshot>(stream, cancellationToken: cancellationToken)
                ?? throw new SoftPilotException("Shell 环境快照损坏，已停止恢复以避免覆盖用户配置。");
        }

        Environment.SetEnvironmentVariable("PATH", snapshot.OriginalPath, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("JAVA_HOME", snapshot.OriginalJavaHome, EnvironmentVariableTarget.User);
        File.Delete(_snapshotPath);
        BroadcastEnvironmentChange();
    }

    private async Task WriteSnapshotAsync(ShellEnvironmentSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temporary = _snapshotPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, _snapshotPath);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string[] SplitPath(string path) => path
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(entry => entry.Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        .Where(entry => entry.Length > 0)
        .ToArray();

    internal static string BuildEnabledPath(string existingPath, string shimsDirectory, string nodeCurrentDirectory)
    {
        var managedPaths = new[] { shimsDirectory, nodeCurrentDirectory };
        var remaining = SplitPath(existingPath)
            .Where(entry => !managedPaths.Contains(entry, StringComparer.OrdinalIgnoreCase));
        return string.Join(Path.PathSeparator, managedPaths.Concat(remaining));
    }

    internal static string BuildDisabledPath(string existingPath, string shimsDirectory, string nodeCurrentDirectory)
    {
        var managedPaths = new[] { shimsDirectory, nodeCurrentDirectory };
        return string.Join(
            Path.PathSeparator,
            SplitPath(existingPath).Where(entry => !managedPaths.Contains(entry, StringComparer.OrdinalIgnoreCase)));
    }

    private static void BroadcastEnvironmentChange()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            0,
            "Environment",
            0x0002,
            5000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nuint wParam,
        string lParam,
        uint flags,
        uint timeout,
        out nuint result);

    private sealed record ShellEnvironmentSnapshot(
        string? OriginalPath,
        string? OriginalJavaHome,
        DateTimeOffset CapturedAt);
}
