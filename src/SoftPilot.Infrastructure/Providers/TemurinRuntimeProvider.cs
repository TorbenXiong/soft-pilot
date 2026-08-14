using System.Text.Json;
using System.Net.Http.Headers;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Providers;

public sealed class TemurinRuntimeProvider : IRuntimeProvider
{
    private const string AdoptiumFingerprint = "3B04D753C9050D9A5D343F39843C48A565F8F04B";
    private static readonly Uri AvailableReleasesUri = new("https://api.adoptium.net/v3/info/available_releases");
    private static readonly Uri PublicKeyUri = new("https://packages.adoptium.net/artifactory/api/gpg/key/public");

    private readonly HttpClient _client;
    private readonly IDownloadService _downloads;
    private readonly ISignatureVerificationService _signatures;
    private readonly IInstallationLayout _layout;
    private readonly ProcessRunner _processRunner;

    public TemurinRuntimeProvider(
        HttpClient client,
        IDownloadService downloads,
        ISignatureVerificationService signatures,
        IInstallationLayout layout,
        ProcessRunner processRunner)
    {
        _client = client;
        _downloads = downloads;
        _signatures = signatures;
        _layout = layout;
        _processRunner = processRunner;
    }

    public RuntimeKind Kind => RuntimeKind.Java;

    public async Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await ProviderUtilities.GetRequiredStringAsync(_client, AvailableReleasesUri, cancellationToken);
        var ltsFeatures = ParseAvailableLtsReleases(metadata);
        var tasks = ltsFeatures.Select(feature => GetFeatureReleasesAsync(feature, cancellationToken));
        return (await Task.WhenAll(tasks))
            .SelectMany(releases => releases)
            .OrderByDescending(release => release.Version, RuntimeVersionComparer.Instance)
            .ToArray();
    }

    internal static IReadOnlyList<int> ParseAvailableLtsReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("available_lts_releases", out var releases)
            || releases.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Adoptium metadata does not contain an available_lts_releases array.");
        }

        var features = new List<int>();
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Number
                || !release.TryGetInt32(out var feature)
                || feature <= 0)
            {
                throw new JsonException("Adoptium metadata contains an invalid LTS feature version.");
            }

            features.Add(feature);
        }

        if (features.Count == 0)
        {
            throw new JsonException("Adoptium metadata contains no available LTS feature versions.");
        }

        return features
            .Distinct()
            .OrderDescending()
            .ToArray();
    }

    public async Task<RuntimeRelease> ResolveAsync(string exactVersion, CancellationToken cancellationToken = default)
    {
        var normalized = ProviderUtilities.NormalizeVersion(exactVersion);
        return (await GetAvailableAsync(cancellationToken))
            .FirstOrDefault(release => string.Equals(release.Version, normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new RuntimeNotFoundException(Kind, exactVersion);
    }

    public async Task PrepareAsync(
        RuntimeRelease release,
        string stagingDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release.Sha256) || release.SignatureUri is null)
        {
            throw new IntegrityException("Adoptium 元数据缺少哈希或签名地址。");
        }

        var (downloadUri, signatureUri) = await ResolveOfficialAssetUrisAsync(
            release.DownloadUri,
            release.SignatureUri,
            cancellationToken);
        var fileName = Path.GetFileName(release.DownloadUri.LocalPath);
        var archivePath = Path.Combine(_layout.DownloadsDirectory, fileName);
        var signaturePath = archivePath + ".sig";
        await _downloads.DownloadAsync(downloadUri, archivePath, release.Sha256, progress, cancellationToken);
        await _downloads.DownloadAsync(signatureUri, signaturePath, cancellationToken: cancellationToken);

        var publicKey = await ProviderUtilities.GetRequiredStringAsync(_client, PublicKeyUri, cancellationToken);
        await _signatures.VerifyDetachedSignatureAsync(
            archivePath,
            signaturePath,
            publicKey,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AdoptiumFingerprint },
            cancellationToken);

        progress?.Report(new OperationProgress("extract", null, fileName));
        SafeZipExtractor.Extract(archivePath, stagingDirectory, stripSingleRootDirectory: true);
    }

    public async Task<RuntimeHealth> CheckHealthAsync(string runtimeDirectory, CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(runtimeDirectory, "bin", "java.exe");
        if (!File.Exists(executable))
        {
            return new RuntimeHealth(false, null, "缺少 bin\\java.exe。");
        }

        var result = await _processRunner.RunAsync(executable, ["-version"], cancellationToken: cancellationToken);
        var output = result.CombinedOutput;
        var version = ExtractQuotedVersion(output);
        return result.ExitCode == 0 && version is not null
            ? new RuntimeHealth(true, version)
            : new RuntimeHealth(false, null, output);
    }

    private async Task<IReadOnlyList<RuntimeRelease>> GetFeatureReleasesAsync(int feature, CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://api.adoptium.net/v3/assets/feature_releases/{feature}/ga" +
            "?architecture=x64&heap_size=normal&image_type=jdk&jvm_impl=hotspot&os=windows&page=0&page_size=50&project=jdk&sort_method=DATE&sort_order=DESC&vendor=eclipse");
        var json = await ProviderUtilities.GetRequiredStringAsync(_client, uri, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var result = new List<RuntimeRelease>();
        foreach (var asset in document.RootElement.EnumerateArray())
        {
            var versionData = asset.GetProperty("version_data");
            var version = ProviderUtilities.ReadFlexibleString(versionData, "semver", "openjdk_version");
            if (version is null)
            {
                continue;
            }

            foreach (var binary in asset.GetProperty("binaries").EnumerateArray())
            {
                var package = binary.GetProperty("package");
                var link = ProviderUtilities.ReadFlexibleString(package, "link");
                var checksum = ProviderUtilities.ReadFlexibleString(package, "checksum");
                var signatureLink = ProviderUtilities.ReadFlexibleString(package, "signature_link");
                if (link is null || checksum is null || signatureLink is null)
                {
                    continue;
                }

                var downloadUri = new Uri(link);

                result.Add(new RuntimeRelease(
                    Kind,
                    version,
                    RuntimeArchitecture.X64,
                    downloadUri,
                    checksum,
                    SignatureUri: new Uri(signatureLink),
                    IsLongTermSupport: true,
                    ReleasePageUri: CreateReleasePageUri(downloadUri)));
            }
        }

        return result;
    }

    internal static Uri? CreateReleasePageUri(Uri downloadUri)
    {
        if (!TryReadGitHubReleasePath(
                downloadUri,
                out var owner,
                out var repository,
                out var tag,
                out _))
        {
            return null;
        }

        return new UriBuilder(Uri.UriSchemeHttps, "github.com")
        {
            Path = $"{owner}/{repository}/releases",
            Fragment = $"release-{tag}",
        }.Uri;
    }

    private async Task<(Uri Archive, Uri Signature)> ResolveOfficialAssetUrisAsync(
        Uri archive,
        Uri signature,
        CancellationToken cancellationToken)
    {
        if (!TryReadGitHubReleasePath(archive, out var owner, out var repository, out var tag, out var archiveName)
            || !TryReadGitHubReleasePath(signature, out _, out _, out var signatureTag, out var signatureName)
            || !string.Equals(tag, signatureTag, StringComparison.Ordinal))
        {
            return (archive, signature);
        }

        var releaseApi = new Uri(
            $"https://api.github.com/repos/{owner}/{repository}/releases/tags/{Uri.EscapeDataString(tag)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("SoftPilot/1.0");
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var assets = document.RootElement.GetProperty("assets")
            .EnumerateArray()
            .Select(asset => new
            {
                Name = asset.GetProperty("name").GetString(),
                ApiUrl = asset.GetProperty("url").GetString(),
            })
            .Where(asset => asset.Name is not null && asset.ApiUrl is not null)
            .ToDictionary(asset => asset.Name!, asset => new Uri(asset.ApiUrl!), StringComparer.Ordinal);

        if (!assets.TryGetValue(archiveName, out var archiveApi)
            || !assets.TryGetValue(signatureName, out var signatureApi))
        {
            throw new IntegrityException("GitHub 官方发布记录缺少 Adoptium 元数据指定的 JDK 资产或签名。");
        }

        var resolved = await Task.WhenAll(
            ResolveGitHubAssetDownloadUriAsync(archiveApi, cancellationToken),
            ResolveGitHubAssetDownloadUriAsync(signatureApi, cancellationToken));
        return (resolved[0], resolved[1]);
    }

    private async Task<Uri> ResolveGitHubAssetDownloadUriAsync(Uri assetApi, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, assetApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.UserAgent.ParseAdd("SoftPilot/1.0");
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var resolved = response.RequestMessage?.RequestUri
            ?? throw new IntegrityException("GitHub 官方资产没有返回下载地址。");
        if (!string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resolved.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrityException($"GitHub 官方资产重定向到未受信任的主机：{resolved.Host}");
        }

        return resolved;
    }

    private static bool TryReadGitHubReleasePath(
        Uri uri,
        out string owner,
        out string repository,
        out string tag,
        out string fileName)
    {
        owner = repository = tag = fileName = string.Empty;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || segments.Length != 6
            || !string.Equals(segments[0], "adoptium", StringComparison.Ordinal)
            || !segments[1].StartsWith("temurin", StringComparison.Ordinal)
            || !segments[1].EndsWith("-binaries", StringComparison.Ordinal)
            || !string.Equals(segments[2], "releases", StringComparison.Ordinal)
            || !string.Equals(segments[3], "download", StringComparison.Ordinal))
        {
            return false;
        }

        owner = segments[0];
        repository = segments[1];
        tag = Uri.UnescapeDataString(segments[4]);
        fileName = Uri.UnescapeDataString(segments[5]);
        return true;
    }

    private static string? ExtractQuotedVersion(string output)
    {
        var firstQuote = output.IndexOf('"');
        var secondQuote = firstQuote < 0 ? -1 : output.IndexOf('"', firstQuote + 1);
        return firstQuote >= 0 && secondQuote > firstQuote ? output[(firstQuote + 1)..secondQuote] : null;
    }
}
