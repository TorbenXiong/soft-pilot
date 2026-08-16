using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SoftPilot.Infrastructure.Installation;

public sealed class PortableToolsProvisioner
{
    public const string ManifestName = "tools.sha256";

    private static readonly string[] RequiredFiles =
    [
        "spt.exe",
        Path.Combine("shims", "SoftPilot.Shim.exe"),
    ];

    private static readonly string[] ShimNames =
    [
        "node", "npm", "npx", "java", "javac", "python", "python3", "pip",
    ];

    public async Task ProvisionAsync(
        Stream archiveStream,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        toolsDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(toolsDirectory));
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry(ManifestName)
            ?? throw new SoftPilotException("内嵌工具负载缺少 tools.sha256。");
        string manifest;
        await using (var stream = manifestEntry.Open())
        using (var reader = new StreamReader(stream))
        {
            manifest = await reader.ReadToEndAsync(cancellationToken);
        }

        if (IsCurrent(toolsDirectory, manifest))
        {
            return;
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var incoming = toolsDirectory + $".incoming-{transactionId}";
        var previous = toolsDirectory + $".previous-{transactionId}";
        var movedPrevious = false;
        var committed = false;
        try
        {
            await ExtractAsync(archive, incoming, cancellationToken);
            await ValidateAsync(incoming, cancellationToken);
            CreateShimAliases(Path.Combine(incoming, "shims"));
            CreateSptAlias(incoming);

            if (Directory.Exists(toolsDirectory))
            {
                Directory.Move(toolsDirectory, previous);
                movedPrevious = true;
            }

            Directory.Move(incoming, toolsDirectory);
            committed = true;
        }
        catch
        {
            if (committed && Directory.Exists(toolsDirectory))
            {
                Directory.Delete(toolsDirectory, recursive: true);
            }

            if (movedPrevious && Directory.Exists(previous) && !Directory.Exists(toolsDirectory))
            {
                Directory.Move(previous, toolsDirectory);
            }

            throw;
        }
        finally
        {
            DeleteDirectoryIfPresent(incoming);
            if (committed)
            {
                DeleteDirectoryIfPresent(previous);
            }
        }
    }

    private static bool IsCurrent(string toolsDirectory, string expectedManifest)
    {
        try
        {
            var manifest = Path.Combine(toolsDirectory, ManifestName);
            return File.Exists(manifest)
                && RequiredFiles.All(file => File.Exists(Path.Combine(toolsDirectory, file)))
                && File.Exists(Path.Combine(toolsDirectory, "shims", "spt.exe"))
                && ShimNames.All(name => File.Exists(Path.Combine(toolsDirectory, "shims", $"{name}.exe")))
                && string.Equals(File.ReadAllText(manifest), expectedManifest, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task ExtractAsync(
        ZipArchive archive,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var destinationPrefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new SoftPilotException($"内嵌工具负载包含不安全路径：{entry.FullName}");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task ValidateAsync(string directory, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifestPath))
        {
            throw new SoftPilotException("内嵌工具负载缺少 tools.sha256。");
        }

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in await File.ReadAllLinesAsync(manifestPath, cancellationToken))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64 || line.Length <= 66 || !line[..64].All(Uri.IsHexDigit))
            {
                throw new SoftPilotException("内嵌工具负载清单格式无效。");
            }

            var relativePath = line[66..].Replace('/', Path.DirectorySeparatorChar);
            if (!expectedFiles.Add(relativePath))
            {
                throw new SoftPilotException($"内嵌工具负载清单包含重复项：{relativePath}");
            }

            var fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
            var prefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                throw new SoftPilotException($"内嵌工具负载缺少文件：{relativePath}");
            }

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(line[..64], actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SoftPilotException($"内嵌工具负载完整性校验失败：{relativePath}");
            }
        }

        var missing = RequiredFiles.Where(file => !expectedFiles.Contains(file)).ToArray();
        if (missing.Length > 0)
        {
            throw new SoftPilotException($"内嵌工具负载不完整，清单缺少：{string.Join("、", missing)}");
        }

        var actualFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file))
            .Where(file => !string.Equals(file, ManifestName, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(expectedFiles))
        {
            throw new SoftPilotException("内嵌工具负载文件集合与清单不一致。");
        }
    }

    private static void CreateShimAliases(string shimsDirectory)
    {
        var source = Path.Combine(shimsDirectory, "SoftPilot.Shim.exe");
        foreach (var name in ShimNames)
        {
            var alias = Path.Combine(shimsDirectory, $"{name}.exe");
            if (!CreateHardLink(alias, source, nint.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法创建 Shell shim：{alias}");
            }
        }
    }

    private static void CreateSptAlias(string toolsDirectory)
    {
        var source = Path.Combine(toolsDirectory, "spt.exe");
        var alias = Path.Combine(toolsDirectory, "shims", "spt.exe");
        if (!CreateHardLink(alias, source, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法创建 spt 命令入口：{alias}");
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}
