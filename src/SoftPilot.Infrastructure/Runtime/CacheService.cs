namespace SoftPilot.Infrastructure.Runtime;

public sealed class CacheService : ICacheService
{
    public static TimeSpan RetentionPeriod { get; } = TimeSpan.FromDays(30);

    private readonly IInstallationLayout _layout;

    public CacheService(IInstallationLayout layout)
    {
        _layout = layout;
    }

    public Task<CacheStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_layout.DownloadsDirectory))
        {
            return Task.FromResult(new CacheStatus(0, 0, _layout.DownloadsDirectory));
        }

        long bytes = 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_layout.DownloadsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += new FileInfo(path).Length;
            count++;
        }

        return Task.FromResult(new CacheStatus(bytes, count, _layout.DownloadsDirectory));
    }

    public Task CleanAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_layout.DownloadsDirectory))
        {
            return Task.CompletedTask;
        }

        foreach (var path in Directory.EnumerateFiles(_layout.DownloadsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }

        foreach (var directory in Directory.EnumerateDirectories(_layout.DownloadsDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(directory, recursive: false);
        }

        return Task.CompletedTask;
    }

    public Task CleanExpiredAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_layout.DownloadsDirectory))
        {
            return Task.CompletedTask;
        }

        var cutoff = DateTime.UtcNow - RetentionPeriod;
        foreach (var path in Directory.EnumerateFiles(_layout.DownloadsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A locked cache entry can be retried on the next startup.
            }
        }

        DeleteEmptyDirectories(cancellationToken, ignoreFailures: true);
        return Task.CompletedTask;
    }

    private void DeleteEmptyDirectories(CancellationToken cancellationToken, bool ignoreFailures)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     _layout.DownloadsDirectory,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception exception) when (
                ignoreFailures && exception is IOException or UnauthorizedAccessException)
            {
                // Non-empty or locked directories remain for the next cleanup.
            }
        }
    }
}
