using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using SoftPilot.Infrastructure.Installation;

namespace SoftPilot.Setup;

internal sealed class InstallerEngine
{
    private const string PayloadResourceName = "SoftPilot.Setup.Payload.zip";

    public async Task InstallAsync(
        string root,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var layout = new WindowsInstallationLayout(root);
        var transactionId = Guid.NewGuid().ToString("N");
        var incoming = Path.Combine(root, $".bin.incoming-{transactionId}");
        var previous = Path.Combine(root, $".bin.previous-{transactionId}");
        var binExisted = Directory.Exists(layout.BinDirectory);
        var swapped = false;

        try
        {
            progress?.Report(new InstallProgress(5, "正在验证安装负载…"));
            Directory.CreateDirectory(root);
            await ExtractPayloadAsync(incoming, cancellationToken);
            ValidatePayload(incoming);

            progress?.Report(new InstallProgress(55, "正在原子替换程序文件…"));
            if (binExisted)
            {
                Directory.Move(layout.BinDirectory, previous);
            }

            Directory.Move(incoming, layout.BinDirectory);
            swapped = true;
            CreateShimAliases(layout.ShimsDirectory);

            progress?.Report(new InstallProgress(75, "正在初始化工作区…"));
            layout.EnsureWorkspace();
            RegisterInstallation(layout);

            progress?.Report(new InstallProgress(90, "正在创建开始菜单快捷方式…"));
            ShortcutService.CreateStartMenuShortcut(Path.Combine(layout.BinDirectory, "SoftPilot.exe"));

            if (Directory.Exists(previous))
            {
                TryDeleteDirectory(previous);
            }

            progress?.Report(new InstallProgress(100, "安装完成。"));
        }
        catch
        {
            if (swapped && Directory.Exists(layout.BinDirectory))
            {
                TryDeleteDirectory(layout.BinDirectory);
            }

            if (Directory.Exists(previous) && !Directory.Exists(layout.BinDirectory))
            {
                Directory.Move(previous, layout.BinDirectory);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(incoming);
            TryDeleteDirectory(previous);
        }
    }

    private static async Task ExtractPayloadAsync(string destination, CancellationToken cancellationToken)
    {
        await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("安装器不包含 SoftPilot 程序负载；请使用正式打包生成的 SoftPilot-Setup.exe。");
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read, leaveOpen: false);
        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装负载包含不安全路径：{entry.FullName}");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static void ValidatePayload(string directory)
    {
        string[] required =
        [
            "SoftPilot.exe",
            "spt.exe",
            "SoftPilot.Uninstall.exe",
            Path.Combine("shims", "SoftPilot.Shim.exe"),
        ];
        var missing = required.Where(file => !File.Exists(Path.Combine(directory, file))).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"安装负载不完整，缺少：{string.Join("、", missing)}");
        }

        var manifestPath = Path.Combine(directory, "payload.sha256");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("安装负载缺少 SHA-256 清单。");
        }

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(manifestPath))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64 || line.Length <= 66)
            {
                throw new InvalidDataException("安装负载 SHA-256 清单格式无效。");
            }

            var expectedHash = line[..64];
            var relativePath = line[66..].Replace('/', Path.DirectorySeparatorChar);
            if (!expectedFiles.Add(relativePath))
            {
                throw new InvalidDataException($"安装负载 SHA-256 清单包含重复项：{relativePath}");
            }

            var fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
            var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                throw new InvalidDataException($"安装负载清单引用无效文件：{relativePath}");
            }

            using var stream = File.OpenRead(fullPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装负载完整性校验失败：{relativePath}");
            }
        }

        var actualFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file))
            .Where(file => !string.Equals(file, "payload.sha256", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(expectedFiles))
        {
            throw new InvalidDataException("安装负载文件集合与 SHA-256 清单不一致。");
        }
    }

    private static void RegisterInstallation(WindowsInstallationLayout layout)
    {
        new WindowsRootRegistry().WriteRoot(layout.Root);
        using var uninstall = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoftPilot");
        uninstall.SetValue("DisplayName", "SoftPilot", RegistryValueKind.String);
        var displayVersion = Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString(3) ?? "0.0.0";
        uninstall.SetValue("DisplayVersion", displayVersion, RegistryValueKind.String);
        uninstall.SetValue("Publisher", "SoftPilot", RegistryValueKind.String);
        uninstall.SetValue("InstallLocation", layout.Root, RegistryValueKind.String);
        uninstall.SetValue("DisplayIcon", Path.Combine(layout.BinDirectory, "SoftPilot.exe"), RegistryValueKind.String);
        uninstall.SetValue("UninstallString", $"\"{Path.Combine(layout.BinDirectory, "SoftPilot.Uninstall.exe")}\"", RegistryValueKind.String);
        uninstall.SetValue("QuietUninstallString", $"\"{Path.Combine(layout.BinDirectory, "SoftPilot.Uninstall.exe")}\" --quiet", RegistryValueKind.String);
        uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShimAliases(string shimsDirectory)
    {
        var source = Path.Combine(shimsDirectory, "SoftPilot.Shim.exe");
        if (!File.Exists(source))
        {
            throw new InvalidDataException("安装负载缺少 SoftPilot.Shim.exe。");
        }

        foreach (var name in new[] { "node", "npm", "npx", "java", "javac", "python", "python3", "pip" })
        {
            var alias = Path.Combine(shimsDirectory, $"{name}.exe");
            if (!CreateHardLink(alias, source, nint.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"无法创建 Shell shim：{alias}");
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record InstallProgress(double Percentage, string Message);

internal static class ShortcutService
{
    public static void CreateStartMenuShortcut(string target)
    {
        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
        Directory.CreateDirectory(startMenuDirectory);
        var shortcutPath = Path.Combine(startMenuDirectory, "SoftPilot.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统不支持创建开始菜单快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 WScript.Shell 对象。");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target)!;
        shortcut.Save();
    }
}
