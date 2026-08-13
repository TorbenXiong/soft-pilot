using System.Text;
using System.Text.Json;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Providers;

public sealed class NodeRuntimeProvider : IRuntimeProvider
{
    private static readonly Uri IndexUri = new("https://nodejs.org/dist/index.json");
    private static readonly Uri KeyBaseUri = new("https://raw.githubusercontent.com/nodejs/release-keys/main/keys/");

    private static readonly string[] TrustedFingerprints =
    [
        "5BE8A3F6C8A5C01D106C0AD820B1A390B168D356",
        "DD792F5973C6DE52C432CBDAC77ABFA00DDBF2B7",
        "CC68F5A3106FF448322E48ED27F5E38D5B0A215F",
        "8FCCA13FEF1D0C2E91008E09770F7A9A5AE15600",
        "890C08DB8579162FEE0DF9DB8BEAB4DFCF555EF4",
        "C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C",
        "108F52B48DB57BB0CC439B2997B01419BD92F80A",
        "655F3B5C1FB3FA8D1A0CA6BDE4A7D232B936D2FD",
        "A363A499291CBBC940DD62E41F10027AF002F8B0",
        "C0D6248439F1D5604AAFFB4021D900FFDB233756",
        "4ED778F539E3634C779C87C6D7062848A1AB005C",
        "141F07595B7B3FFE74309A937405533BE57C7D57",
        "9554F04D7259F04124DE6B476D5A82AC7E37093B",
        "94AE36675C464D64BAFA68DD7434390BDBE9B9C5",
        "1C050899334244A8AF75E53792EF661D867B9DFA",
        "74F12602B6F1C4E913FAA37AD3A89613643B6201",
        "B9AE9905FFD7803F25714661B63B535A4C206CA9",
        "77984A986EBC2AA786BC0F66B01FBB92821C587A",
        "93C7E9E91B49E432C2F75674B0A78B0A6C481CF6",
        "56730D5401028683275BD23C23EFEFE93C4CFFFE",
        "71DCFD284A79C3B38668286BC97EC7A07EDE3FC1",
        "FD3A5288F042B6850C66B31F09FE44734EB7990E",
        "61FC681DFB92A079F1685E77973F295594EC4689",
        "114F43EE0176B71C7BC219DD50A3051F888C628D",
        "C4F0DFFF4E8C1A8236409D08E73BC641CC11F4C8",
        "DD8F2338BAE7501E3DD5AC78C273792F7D83545D",
        "A48C2BEE680E841632CD4E44F07496B3EB3C1762",
        "B9E2F5981AA6E0CD28160D9FF13993A75599653C",
        "7937DFD2AB06298B2293C3187D33FF9D0246406D",
    ];

    private readonly HttpClient _client;
    private readonly IDownloadService _downloads;
    private readonly ISignatureVerificationService _signatures;
    private readonly IInstallationLayout _layout;
    private readonly ProcessRunner _processRunner;

    public NodeRuntimeProvider(
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

    public RuntimeKind Kind => RuntimeKind.Node;

    public async Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        var json = await ProviderUtilities.GetRequiredStringAsync(_client, IndexUri, cancellationToken);
        return ParseReleases(json);
    }

    internal static IReadOnlyList<RuntimeRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        var releases = new List<RuntimeRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var rawVersion = item.GetProperty("version").GetString();
            if (rawVersion is null)
            {
                continue;
            }

            if (!item.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var files = filesElement.EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
            if (!files.Contains("win-x64-zip"))
            {
                continue;
            }

            var version = ProviderUtilities.NormalizeVersion(rawVersion);
            var directory = new Uri($"https://nodejs.org/dist/v{version}/");
            var fileName = $"node-v{version}-win-x64.zip";
            var isLts = item.TryGetProperty("lts", out var lts) && lts.ValueKind == JsonValueKind.String;
            releases.Add(new RuntimeRelease(
                RuntimeKind.Node,
                version,
                RuntimeArchitecture.X64,
                new Uri(directory, fileName),
                null,
                new Uri(directory, "SHASUMS256.txt"),
                new Uri(directory, "SHASUMS256.txt.sig"),
                isLts));
        }

        return releases
            .DistinctBy(release => release.Version)
            .OrderByDescending(release => release.Version, RuntimeVersionComparer.Instance)
            .ToArray();
    }

    public async Task<RuntimeRelease> ResolveAsync(string exactVersion, CancellationToken cancellationToken = default)
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
        var fileName = Path.GetFileName(release.DownloadUri.LocalPath);
        var checksumPath = Path.Combine(_layout.DownloadsDirectory, $"node-{release.Version}-SHASUMS256.txt");
        var signaturePath = checksumPath + ".sig";
        var archivePath = Path.Combine(_layout.DownloadsDirectory, fileName);

        progress?.Report(new OperationProgress("metadata", null, "验证 Node.js 官方校验清单"));
        await _downloads.DownloadAsync(release.ChecksumUri!, checksumPath, cancellationToken: cancellationToken);
        await _downloads.DownloadAsync(release.SignatureUri!, signaturePath, cancellationToken: cancellationToken);
        var keyMaterial = await LoadTrustedKeysAsync(cancellationToken);
        await _signatures.VerifyDetachedSignatureAsync(
            checksumPath,
            signaturePath,
            keyMaterial,
            TrustedFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken);

        var checksums = await File.ReadAllTextAsync(checksumPath, cancellationToken);
        var expectedHash = ProviderUtilities.FindChecksum(checksums, fileName);
        await _downloads.DownloadAsync(release.DownloadUri, archivePath, expectedHash, progress, cancellationToken);

        progress?.Report(new OperationProgress("extract", null, fileName));
        SafeZipExtractor.Extract(archivePath, stagingDirectory, stripSingleRootDirectory: true);
    }

    public async Task<RuntimeHealth> CheckHealthAsync(string runtimeDirectory, CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(runtimeDirectory, "node.exe");
        if (!File.Exists(executable))
        {
            return new RuntimeHealth(false, null, "缺少 node.exe。");
        }

        var npmCli = Path.Combine(runtimeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        var npxCli = Path.Combine(runtimeDirectory, "node_modules", "npm", "bin", "npx-cli.js");
        if (!File.Exists(npmCli) || !File.Exists(npxCli))
        {
            return new RuntimeHealth(false, null, "Node.js 运行时缺少 npm 或 npx。");
        }

        var result = await _processRunner.RunAsync(executable, ["--version"], cancellationToken: cancellationToken);
        var version = ProviderUtilities.NormalizeVersion(result.StandardOutput.Trim());
        if (result.ExitCode != 0 || version.Length == 0)
        {
            return new RuntimeHealth(false, null, result.CombinedOutput);
        }

        var npm = await _processRunner.RunAsync(executable, [npmCli, "--version"], cancellationToken: cancellationToken);
        if (npm.ExitCode != 0 || npm.StandardOutput.Trim().Length == 0)
        {
            return new RuntimeHealth(false, null, $"npm 健康检查失败：{npm.CombinedOutput}");
        }

        var npx = await _processRunner.RunAsync(executable, [npxCli, "--version"], cancellationToken: cancellationToken);
        return npx.ExitCode == 0 && npx.StandardOutput.Trim().Length > 0
            ? new RuntimeHealth(true, version)
            : new RuntimeHealth(false, null, $"npx 健康检查失败：{npx.CombinedOutput}");
    }

    private async Task<string> LoadTrustedKeysAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var fingerprint in TrustedFingerprints)
        {
            var uri = new Uri(KeyBaseUri, $"{fingerprint}.asc");
            try
            {
                builder.AppendLine(await ProviderUtilities.GetRequiredStringAsync(_client, uri, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // A rotated or temporarily absent key must not relax verification of the remaining pinned keys.
            }
        }

        if (builder.Length == 0)
        {
            throw new IntegrityException("无法从 Node.js 官方 release-keys 仓库取得任何受信任公钥。");
        }

        return builder.ToString();
    }
}
