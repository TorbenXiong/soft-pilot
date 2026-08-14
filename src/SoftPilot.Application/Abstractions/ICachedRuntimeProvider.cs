namespace SoftPilot.Application.Abstractions;

public interface ICachedRuntimeProvider : IRuntimeProvider
{
    Task<RuntimeCatalogCacheEntry?> GetCachedCatalogAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimeRelease>> RefreshAvailableAsync(
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeCatalogCacheEntry(
    RuntimeKind Kind,
    DateTimeOffset RefreshedAt,
    IReadOnlyList<RuntimeRelease> Releases)
{
    public bool IsFresh(DateTimeOffset now, TimeSpan lifetime) =>
        RefreshedAt <= now.AddMinutes(5)
        && now - RefreshedAt < lifetime;
}
