namespace SoftPilot.Infrastructure.Providers;

public sealed class CachedRuntimeProvider : ICachedRuntimeProvider
{
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromDays(1);
    private readonly IRuntimeProvider _inner;
    private readonly RuntimeCatalogCache _cache;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    internal CachedRuntimeProvider(IRuntimeProvider inner, RuntimeCatalogCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public RuntimeKind Kind => _inner.Kind;

    public Task<RuntimeCatalogCacheEntry?> GetCachedCatalogAsync(
        CancellationToken cancellationToken = default) =>
        _cache.LoadAsync(Kind, cancellationToken);

    public async Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        var cached = await _cache.LoadAsync(Kind, cancellationToken);
        if (cached?.IsFresh(DateTimeOffset.UtcNow, CatalogLifetime) == true)
        {
            return cached.Releases;
        }

        return await RefreshCoreAsync(force: false, cancellationToken);
    }

    public Task<IReadOnlyList<RuntimeRelease>> RefreshAvailableAsync(
        CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(force: true, cancellationToken);

    public async Task<RuntimeRelease> ResolveAsync(
        string exactVersion,
        CancellationToken cancellationToken = default)
    {
        var normalized = ProviderUtilities.NormalizeVersion(exactVersion);
        return (await GetAvailableAsync(cancellationToken))
            .FirstOrDefault(release => string.Equals(
                release.Version,
                normalized,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new RuntimeNotFoundException(Kind, exactVersion);
    }

    public Task PrepareAsync(
        RuntimeRelease release,
        string stagingDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _inner.PrepareAsync(release, stagingDirectory, progress, cancellationToken);

    public Task<RuntimeHealth> CheckHealthAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken = default) =>
        _inner.CheckHealthAsync(runtimeDirectory, cancellationToken);

    private async Task<IReadOnlyList<RuntimeRelease>> RefreshCoreAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!force)
            {
                var cached = await _cache.LoadAsync(Kind, cancellationToken);
                if (cached?.IsFresh(DateTimeOffset.UtcNow, CatalogLifetime) == true)
                {
                    return cached.Releases;
                }
            }

            var releases = await _inner.GetAvailableAsync(cancellationToken);
            await _cache.SaveAsync(
                new RuntimeCatalogCacheEntry(Kind, DateTimeOffset.UtcNow, releases),
                cancellationToken);
            return releases;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
