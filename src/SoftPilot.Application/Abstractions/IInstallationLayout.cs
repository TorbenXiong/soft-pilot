namespace SoftPilot.Application.Abstractions;

public interface IInstallationLayout
{
    string Root { get; }
    string ManagementDirectory { get; }
    string ToolsDirectory { get; }
    string ShimsDirectory { get; }
    string AppDirectory { get; }
    string CurrentDirectory { get; }
    string DataDirectory { get; }
    string DownloadsDirectory { get; }
    string StagingDirectory { get; }
    string TrashDirectory { get; }
    string LogsDirectory { get; }
    string GitDirectory { get; }
    string CocosDirectory { get; }
    string CocosCreatorDirectory { get; }

    string GetRuntimeDirectory(RuntimeKind kind, string version);
    string GetCurrentLink(RuntimeKind kind);
    string GetTrashDirectory(RuntimeKind kind, string version, DateTimeOffset deletedAt);
    string GetRedisDataDirectory(string version);
    string GetRedisLogPath(string version);
    string GetRedisServiceStatePath();
    string GetMySqlDataDirectory(string version);
    string GetMySqlLogPath(string version);
    string GetMySqlConfigPath(string version);
    string GetCocosCreatorDirectory(string version);
    string GetCocosCreatorDownloadDirectory(string version);
    void EnsureWorkspace();
}
