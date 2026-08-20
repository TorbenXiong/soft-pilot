namespace SoftPilot.Infrastructure.Installation;

public sealed class WindowsInstallationLayout : IInstallationLayout
{
    public const string ManagementDirectoryName = "SoftPilotData";
    public const string WorkspaceMarkerName = ".softpilot-root";

    public WindowsInstallationLayout(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }
    public string ManagementDirectory => Path.Combine(Root, ManagementDirectoryName);
    public string ToolsDirectory => Path.Combine(ManagementDirectory, "tools");
    public string ShimsDirectory => Path.Combine(ToolsDirectory, "shims");
    public string AppDirectory => Path.Combine(ManagementDirectory, "app");
    public string CurrentDirectory => Path.Combine(ManagementDirectory, "current");
    public string DataDirectory => Path.Combine(ManagementDirectory, "data");
    public string DownloadsDirectory => Path.Combine(ManagementDirectory, "cache", "downloads");
    public string StagingDirectory => Path.Combine(ManagementDirectory, "staging");
    public string TrashDirectory => Path.Combine(ManagementDirectory, "trash");
    public string LogsDirectory => Path.Combine(ManagementDirectory, "logs");

    public string GetRuntimeDirectory(RuntimeKind kind, string version) =>
        Path.Combine(AppDirectory, GetKindName(kind), version);

    public string GetCurrentLink(RuntimeKind kind) =>
        Path.Combine(CurrentDirectory, GetKindName(kind));

    public string GetTrashDirectory(RuntimeKind kind, string version, DateTimeOffset deletedAt) =>
        Path.Combine(TrashDirectory, GetKindName(kind), $"{version}-{deletedAt:yyyyMMddHHmmssfff}");

    public string GetRedisDataDirectory(string version) =>
        Path.Combine(DataDirectory, "redis", version);

    public string GetRedisLogPath(string version) =>
        Path.Combine(LogsDirectory, "redis", version, "redis.log");

    public void EnsureWorkspace()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ManagementDirectory);
        Directory.CreateDirectory(AppDirectory);
        Directory.CreateDirectory(CurrentDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(TrashDirectory);
        Directory.CreateDirectory(LogsDirectory);

        var marker = Path.Combine(ManagementDirectory, WorkspaceMarkerName);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "SoftPilot workspace\n");
        }
    }

    public static string GetKindName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => "node",
        RuntimeKind.Java => "java",
        RuntimeKind.Python => "python",
        RuntimeKind.Redis => "redis",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
