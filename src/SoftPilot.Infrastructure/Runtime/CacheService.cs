namespace SoftPilot.Infrastructure.Runtime;

public sealed class CacheService : ICacheService
{
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
}
