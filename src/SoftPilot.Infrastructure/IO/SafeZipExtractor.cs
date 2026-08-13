using System.IO.Compression;

namespace SoftPilot.Infrastructure.IO;

public static class SafeZipExtractor
{
    public static void Extract(string archivePath, string destinationDirectory, bool stripSingleRootDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        var rootPrefix = stripSingleRootDirectory ? GetSingleRootPrefix(archive.Entries) : null;
        foreach (var entry in archive.Entries)
        {
            var relativeName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (rootPrefix is not null)
            {
                if (!relativeName.StartsWith(rootPrefix, StringComparison.Ordinal))
                {
                    throw new IntegrityException("ZIP 内容不具有一致的根目录。");
                }

                relativeName = relativeName[rootPrefix.Length..];
            }

            if (relativeName.Length == 0)
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, relativeName));
            if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new IntegrityException($"ZIP 条目试图越过暂存目录：{entry.FullName}");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: false);
        }
    }

    private static string? GetSingleRootPrefix(IEnumerable<ZipArchiveEntry> entries)
    {
        var roots = entries
            .Select(entry => entry.FullName.Split('/', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(root => root is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return roots.Length == 1 ? roots[0]!.Replace('/', Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar : null;
    }
}
