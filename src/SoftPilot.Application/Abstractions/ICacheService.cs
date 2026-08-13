namespace SoftPilot.Application.Abstractions;

public interface ICacheService
{
    Task<CacheStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task CleanAsync(CancellationToken cancellationToken = default);
}

public sealed record CacheStatus(long Bytes, int FileCount, string Path);
