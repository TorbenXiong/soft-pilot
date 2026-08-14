using System.Text.Json;

namespace SoftPilot.Infrastructure.Providers;

internal sealed class RuntimeCatalogCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _directory;

    public RuntimeCatalogCache(IInstallationLayout layout)
    {
        _directory = Path.Combine(
            Path.GetDirectoryName(layout.DownloadsDirectory)
                ?? throw new InvalidOperationException("无法确定缓存根目录。"),
            "catalog");
    }

    public async Task<RuntimeCatalogCacheEntry?> LoadAsync(
        RuntimeKind kind,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(kind);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<RuntimeCatalogCacheEntry>(
                stream,
                SerializerOptions,
                cancellationToken);
            return entry is not null
                && entry.Kind == kind
                && entry.Releases.All(release =>
                    release.Kind == kind
                    && string.Equals(release.DownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && release.ReleasePageUri is not null
                    && string.Equals(
                        release.ReleasePageUri.Scheme,
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase))
                    ? entry
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        RuntimeCatalogCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var path = GetPath(entry.Kind);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, entry, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetPath(RuntimeKind kind) =>
        Path.Combine(_directory, $"{kind.ToString().ToLowerInvariant()}.json");
}
