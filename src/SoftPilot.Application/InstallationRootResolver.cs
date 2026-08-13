namespace SoftPilot.Application;

public static class InstallationRootResolver
{
    public const string ProductDirectoryName = "SoftPilot";

    public static string Resolve(string selectedParentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedParentDirectory);

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedParentDirectory));
        var name = GetLastComponent(normalized);

        return string.Equals(name, ProductDirectoryName, StringComparison.Ordinal)
            ? normalized
            : Path.Join(normalized, ProductDirectoryName);
    }

    private static string GetLastComponent(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null && string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return Path.GetFileName(path);
    }
}
