using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Shell;

namespace SoftPilot.Uninstall;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--cleanup")
        {
            Shutdown(await CleanupAsync(e.Args));
            return;
        }

        var quiet = e.Args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        var deleteWorkspace = e.Args.Contains("--delete-workspace", StringComparer.OrdinalIgnoreCase);
        if (!quiet)
        {
            var window = new UninstallWindow();
            if (window.ShowDialog() != true)
            {
                Shutdown(1);
                return;
            }

            deleteWorkspace = window.DeleteWorkspace;
        }

        try
        {
            var root = new WindowsRootRegistry().ReadRoot()
                ?? throw new InvalidOperationException("没有找到 SoftPilot 安装位置。");
            var layout = new WindowsInstallationLayout(root);
            await new WindowsShellIntegrationService(layout).DisableAsync();
            RegistrationService.Remove(deleteWorkspace);
            LaunchCleanup(root, deleteWorkspace);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            if (!quiet)
            {
                MessageBox.Show(exception.Message, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown(1);
        }
    }

    private static void LaunchCleanup(string root, bool deleteWorkspace)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定卸载器路径。");
        var temporary = Path.Combine(Path.GetTempPath(), $"SoftPilot-Uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(source, temporary);
        var start = new ProcessStartInfo
        {
            FileName = temporary,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--cleanup");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (deleteWorkspace)
        {
            start.ArgumentList.Add("--delete-workspace");
        }

        _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动卸载清理程序。");
    }

    private static async Task<int> CleanupAsync(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var parentProcessId))
        {
            return 2;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
        var deleteWorkspace = args.Contains("--delete-workspace", StringComparer.OrdinalIgnoreCase);
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
        }

        await Task.Delay(300);
        try
        {
            if (deleteWorkspace)
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            else
            {
                DeleteDirectoryIfPresent(Path.Combine(root, "bin"));
                foreach (var candidate in Directory.EnumerateDirectories(root, ".bin.*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(candidate);
                    if (name.StartsWith(".bin.previous-", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(".bin.incoming-", StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteDirectoryIfPresent(candidate);
                    }
                }
            }

            _ = MoveFileEx(Environment.ProcessPath, null, 0x00000004);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"部分文件未能删除，请关闭占用 SoftPilot 的程序后手工删除：\n{root}\n\n{exception.Message}",
                "卸载未完全结束",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return 1;
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string? existingFileName, string? newFileName, uint flags);
}

internal static class RegistrationService
{
    public static void Remove(bool deleteWorkspace)
    {
        if (deleteWorkspace)
        {
            new WindowsRootRegistry().DeleteRoot();
        }

        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoftPilot",
            throwOnMissingSubKey: false);
        var startMenuShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "SoftPilot.lnk");
        DeleteShortcutIfPresent(startMenuShortcut);
        DeleteShortcutIfPresent(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "SoftPilot.lnk"));
    }

    private static void DeleteShortcutIfPresent(string shortcut)
    {
        if (File.Exists(shortcut))
        {
            File.Delete(shortcut);
        }
    }
}
