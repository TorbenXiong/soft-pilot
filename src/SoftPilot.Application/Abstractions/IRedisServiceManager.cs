namespace SoftPilot.Application.Abstractions;

public interface IRedisServiceManager
{
    Task<RedisServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<RedisServiceStatus> StartAsync(string version, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record RedisServiceStatus(
    bool IsRunning,
    string? Version = null,
    int? ProcessId = null,
    DateTimeOffset? StartedAt = null,
    string? ConfigPath = null,
    string? DataPath = null,
    string? LogPath = null,
    string? Problem = null);
