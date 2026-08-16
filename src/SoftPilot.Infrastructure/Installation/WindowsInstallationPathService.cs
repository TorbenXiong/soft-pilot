using Microsoft.Win32;

namespace SoftPilot.Infrastructure.Installation;

public sealed class WindowsInstallationPathService : IInstallationPathService
{
    public string GetDefaultParentDirectory()
    {
        var fallbackParent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        var driveRoots = OrderDriveRootCandidates(
            DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName));

        return FindFirstValidParentDirectory(driveRoots, fallbackParent);
    }

    public string ResolveRoot(string selectedParentDirectory) =>
        InstallationRootResolver.Resolve(selectedParentDirectory);

    public InstallationPathValidation Validate(string selectedParentDirectory)
    {
        var errors = new List<string>();
        string selectedParent;
        string finalRoot;

        try
        {
            selectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedParentDirectory));
            finalRoot = ResolveRoot(selectedParent);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new InstallationPathValidation(
                selectedParentDirectory,
                string.Empty,
                false,
                [$"路径无效：{exception.Message}"]);
        }

        if (!Path.IsPathFullyQualified(finalRoot) || finalRoot.StartsWith("\\\\", StringComparison.Ordinal))
        {
            errors.Add("必须选择本地绝对路径，不能使用 UNC 或网络路径。");
        }

        var root = Path.GetPathRoot(finalRoot);
        if (string.IsNullOrEmpty(root))
        {
            errors.Add("无法确定目标磁盘。");
        }
        else
        {
            ValidateDrive(root, errors);
        }

        ValidateProtectedDirectories(finalRoot, errors);
        ValidateCloudDirectories(finalRoot, errors);
        ValidateExistingTarget(finalRoot, errors);

        if (errors.Count == 0)
        {
            ValidateWritable(finalRoot, errors);
        }

        return new InstallationPathValidation(selectedParent, finalRoot, errors.Count == 0, errors);
    }

    internal string FindFirstValidParentDirectory(
        IEnumerable<string> candidates,
        string fallbackParent)
    {
        foreach (var candidate in candidates
                     .Append(fallbackParent)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var validation = Validate(candidate);
            if (validation.IsValid)
            {
                return validation.SelectedParent;
            }
        }

        return fallbackParent;
    }

    internal static IReadOnlyList<string> OrderDriveRootCandidates(IEnumerable<string> driveRoots) =>
        driveRoots
            .Where(root => root.Length >= 2
                && root[1] == Path.VolumeSeparatorChar
                && char.ToUpperInvariant(root[0]) is >= 'C' and <= 'Z')
            .OrderBy(root => char.ToUpperInvariant(root[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateDrive(string root, ICollection<string> errors)
    {
        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                errors.Add("目标磁盘尚未就绪。");
                return;
            }

            if (drive.DriveType != DriveType.Fixed)
            {
                errors.Add("目标必须位于本地固定磁盘，不能使用网络盘或可移动盘。");
            }

            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("目标磁盘必须使用 NTFS 文件系统。");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"无法读取目标磁盘信息：{exception.Message}");
        }
    }

    private static void ValidateProtectedDirectories(string target, ICollection<string> errors)
    {
        var protectedPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };

        if (protectedPaths.Any(path => !string.IsNullOrWhiteSpace(path) && IsSameOrDescendant(target, path)))
        {
            errors.Add("最终目录不能位于 Windows、Program Files 或 ProgramData 等系统管理目录中。");
        }
    }

    private static void ValidateCloudDirectories(string target, ICollection<string> errors)
    {
        var cloudRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(cloudRoots, Environment.GetEnvironmentVariable("OneDrive"));
        AddIfPresent(cloudRoots, Environment.GetEnvironmentVariable("OneDriveConsumer"));
        AddIfPresent(cloudRoots, Environment.GetEnvironmentVariable("OneDriveCommercial"));

        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
        if (key is not null)
        {
            foreach (var name in key.GetValueNames().Where(name => name.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)))
            {
                AddIfPresent(cloudRoots, Environment.ExpandEnvironmentVariables(key.GetValue(name)?.ToString() ?? string.Empty));
            }
        }

        if (cloudRoots.Any(root => IsSameOrDescendant(target, root)))
        {
            errors.Add("最终目录不能位于已知云同步目录中。");
        }
    }

    private static void ValidateExistingTarget(string target, ICollection<string> errors)
    {
        if (File.Exists(target))
        {
            errors.Add("最终路径已被同名文件占用。");
            return;
        }

        if (!Directory.Exists(target))
        {
            return;
        }

        var entries = Directory.EnumerateFileSystemEntries(target).ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        var isPortableRoot = entries.All(entry =>
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, "SoftPilot.exe", StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(entry);
            }

            if (string.Equals(name, WindowsInstallationLayout.ManagementDirectoryName, StringComparison.Ordinal))
            {
                return HasWorkspaceMarker(entry);
            }

            return name.StartsWith(".SoftPilot.previous-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        });
        if (!isPortableRoot)
        {
            errors.Add("最终目录非空且不属于 SoftPilot，可能已被其他应用占用。");
        }
    }

    private static bool HasWorkspaceMarker(string managementDirectory)
    {
        try
        {
            var marker = Path.Combine(managementDirectory, WindowsInstallationLayout.WorkspaceMarkerName);
            return File.Exists(marker)
                && string.Equals(
                    File.ReadAllText(marker).Trim(),
                    "SoftPilot workspace",
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ValidateWritable(string target, ICollection<string> errors)
    {
        var existingAncestor = target;
        while (!Directory.Exists(existingAncestor))
        {
            existingAncestor = Path.GetDirectoryName(existingAncestor) ?? string.Empty;
            if (existingAncestor.Length == 0)
            {
                errors.Add("找不到可写的父目录。");
                return;
            }
        }

        var probe = Path.Combine(existingAncestor, $".softpilot-write-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"当前用户无法写入目标目录：{exception.Message}");
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfPresent(ISet<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
        {
            paths.Add(Path.GetFullPath(path));
        }
    }
}
