namespace SoftPilot.Application.Abstractions;

public interface IMySqlServiceManager
{
    Task<IReadOnlyList<MySqlServiceStatus>> GetStatusesAsync(CancellationToken cancellationToken = default);
    Task<MySqlServiceStatus> GetStatusAsync(string version, CancellationToken cancellationToken = default);
    Task<MySqlServiceStatus> StartAsync(string version, CancellationToken cancellationToken = default);
    Task StopAsync(string version, CancellationToken cancellationToken = default);
    Task<MySqlCredentials> GetCredentialsAsync(string version, CancellationToken cancellationToken = default);
    int GetConfiguredPort(string version);
    Task SetConfiguredPortAsync(string version, int port, CancellationToken cancellationToken = default);
}

public sealed record MySqlServiceStatus(
    bool IsRunning,
    string? Version = null,
    int? ProcessId = null,
    DateTimeOffset? StartedAt = null,
    string? ConfigPath = null,
    string? DataPath = null,
    string? LogPath = null,
    string? Problem = null,
    int Port = 3306);

public sealed record MySqlCredentials(string Host, int Port, string UserName, string Password);
