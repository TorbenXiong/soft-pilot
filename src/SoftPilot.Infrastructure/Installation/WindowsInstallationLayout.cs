namespace SoftPilot.Infrastructure.Installation;

public sealed class WindowsInstallationLayout : IInstallationLayout
{
    public WindowsInstallationLayout(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }
    public string BinDirectory => Path.Combine(Root, "bin");
    public string ShimsDirectory => Path.Combine(BinDirectory, "shims");
    public string AppDirectory => Path.Combine(Root, "app");
    public string CurrentDirectory => Path.Combine(Root, "current");
    public string DataDirectory => Path.Combine(Root, "data");
    public string DownloadsDirectory => Path.Combine(Root, "cache", "downloads");
    public string StagingDirectory => Path.Combine(Root, "staging");
    public string TrashDirectory => Path.Combine(Root, "trash");
    public string LogsDirectory => Path.Combine(Root, "logs");

    public string GetRuntimeDirectory(RuntimeKind kind, string version) =>
        Path.Combine(AppDirectory, GetKindName(kind), version);

    public string GetCurrentLink(RuntimeKind kind) =>
        Path.Combine(CurrentDirectory, GetKindName(kind));

    public string GetTrashDirectory(RuntimeKind kind, string version, DateTimeOffset deletedAt) =>
        Path.Combine(TrashDirectory, GetKindName(kind), $"{version}-{deletedAt:yyyyMMddHHmmssfff}");

    public void EnsureWorkspace()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BinDirectory);
        Directory.CreateDirectory(ShimsDirectory);
        Directory.CreateDirectory(AppDirectory);
        Directory.CreateDirectory(CurrentDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(TrashDirectory);
        Directory.CreateDirectory(LogsDirectory);

        var marker = Path.Combine(Root, ".softpilot-root");
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
