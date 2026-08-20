using System.Text.Json;
using System.Text.RegularExpressions;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Providers;

public sealed partial class RedisRuntimeProvider : IRuntimeProvider
{
    private static readonly Uri WindowsReleasesUri = new(
        "https://api.github.com/repos/redis-windows/redis-windows/releases?per_page=100");
    private static readonly Uri OfficialReleasesUri = new(
        "https://api.github.com/repos/redis/redis/releases?per_page=100");
    private const string WindowsDownloadPrefix = "/redis-windows/redis-windows/releases/download/";

    private readonly HttpClient _client;
    private readonly IDownloadService _downloads;
    private readonly IInstallationLayout _layout;
    private readonly ProcessRunner _processRunner;

    public RedisRuntimeProvider(
        HttpClient client,
        IDownloadService downloads,
        IInstallationLayout layout,
        ProcessRunner processRunner)
    {
        _client = client;
        _downloads = downloads;
        _layout = layout;
        _processRunner = processRunner;
    }

    public RuntimeKind Kind => RuntimeKind.Redis;

    public async Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        var windowsTask = ProviderUtilities.GetRequiredStringAsync(
            _client,
            WindowsReleasesUri,
            cancellationToken);
        var officialTask = ProviderUtilities.GetRequiredStringAsync(
            _client,
            OfficialReleasesUri,
            cancellationToken);
        await Task.WhenAll(windowsTask, officialTask);
        return ParseReleases(await windowsTask, await officialTask);
    }

    internal static IReadOnlyList<RuntimeRelease> ParseReleases(
        string windowsReleasesJson,
        string officialReleasesJson)
    {
        using var officialDocument = JsonDocument.Parse(officialReleasesJson);
        var officialVersions = officialDocument.RootElement
            .EnumerateArray()
            .Where(item => !ReadBoolean(item, "draft") && !ReadBoolean(item, "prerelease"))
            .Select(item => ProviderUtilities.NormalizeVersion(
                ProviderUtilities.ReadFlexibleString(item, "tag_name") ?? string.Empty))
            .Where(version => StableVersionPattern().IsMatch(version))
            .ToHashSet(StringComparer.Ordinal);

        using var windowsDocument = JsonDocument.Parse(windowsReleasesJson);
        var releases = new List<RuntimeRelease>();
        foreach (var item in windowsDocument.RootElement.EnumerateArray())
        {
            if (ReadBoolean(item, "draft") || ReadBoolean(item, "prerelease"))
            {
                continue;
            }

            var rawTag = ProviderUtilities.ReadFlexibleString(item, "tag_name");
            var version = ProviderUtilities.NormalizeVersion(rawTag ?? string.Empty);
            if (!officialVersions.Contains(version))
            {
                continue;
            }

            var expectedName = $"Redis-{version}-Windows-x64-msys2.zip";
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(
                        ProviderUtilities.ReadFlexibleString(asset, "name"),
                        expectedName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var address = ProviderUtilities.ReadFlexibleString(asset, "browser_download_url");
                var digest = ProviderUtilities.ReadFlexibleString(asset, "digest");
                if (!Uri.TryCreate(address, UriKind.Absolute, out var downloadUri)
                    || !IsTrustedDownloadUri(downloadUri)
                    || digest is null
                    || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    || !Sha256Pattern().IsMatch(digest[7..]))
                {
                    continue;
                }

                releases.Add(new RuntimeRelease(
                    RuntimeKind.Redis,
                    version,
                    RuntimeArchitecture.X64,
                    downloadUri,
                    digest[7..].ToLowerInvariant(),
                    IsLongTermSupport: false,
                    ReleasePageUri: new Uri($"https://github.com/redis/redis/releases/tag/{Uri.EscapeDataString(rawTag!)}")));
                break;
            }
        }

        return releases
            .DistinctBy(release => release.Version)
            .OrderByDescending(release => release.Version, RuntimeVersionComparer.Instance)
            .ToArray();
    }

    public async Task<RuntimeRelease> ResolveAsync(
        string exactVersion,
        CancellationToken cancellationToken = default)
    {
        var normalized = ProviderUtilities.NormalizeVersion(exactVersion);
        return (await GetAvailableAsync(cancellationToken))
            .FirstOrDefault(release => string.Equals(release.Version, normalized, StringComparison.Ordinal))
            ?? throw new RuntimeNotFoundException(Kind, exactVersion);
    }

    public async Task PrepareAsync(
        RuntimeRelease release,
        string stagingDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release.Sha256))
        {
            throw new IntegrityException("Redis Windows 归档缺少 GitHub SHA-256 摘要。");
        }

        var fileName = Path.GetFileName(release.DownloadUri.LocalPath);
        var archivePath = Path.Combine(_layout.DownloadsDirectory, fileName);
        progress?.Report(new OperationProgress("metadata", null, "验证 Redis 官方版本与社区 Windows 构建摘要"));
        await _downloads.DownloadAsync(
            release.DownloadUri,
            archivePath,
            release.Sha256,
            progress,
            cancellationToken);
        progress?.Report(new OperationProgress("extract", null, fileName));
        SafeZipExtractor.Extract(archivePath, stagingDirectory, stripSingleRootDirectory: true);
    }

    public async Task<RuntimeHealth> CheckHealthAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken = default)
    {
        var server = Path.Combine(runtimeDirectory, "redis-server.exe");
        var client = Path.Combine(runtimeDirectory, "redis-cli.exe");
        if (!File.Exists(server) || !File.Exists(client))
        {
            return new RuntimeHealth(false, null, "Redis Windows 运行时缺少 redis-server.exe 或 redis-cli.exe。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var serverResult = await _processRunner.RunAsync(server, ["--version"], cancellationToken: timeout.Token);
            var match = ServerVersionPattern().Match(serverResult.CombinedOutput);
            if (serverResult.ExitCode != 0 || !match.Success)
            {
                return new RuntimeHealth(false, null, serverResult.CombinedOutput);
            }

            var clientResult = await _processRunner.RunAsync(client, ["--version"], cancellationToken: timeout.Token);
            return clientResult.ExitCode == 0
                ? new RuntimeHealth(true, match.Groups[1].Value)
                : new RuntimeHealth(false, null, $"redis-cli 健康检查失败：{clientResult.CombinedOutput}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeHealth(false, null, "Redis 健康检查超时（30 秒）。");
        }
    }

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsTrustedDownloadUri(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(WindowsDownloadPrefix, StringComparison.Ordinal);

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"(?:Redis server )?v=(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ServerVersionPattern();
}
