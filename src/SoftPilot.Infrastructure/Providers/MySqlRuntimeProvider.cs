using System.Text;
using System.Text.RegularExpressions;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Providers;

public sealed partial class MySqlRuntimeProvider : IRuntimeProvider
{
    private static readonly Uri Key2022Uri = new("https://repo.mysql.com/RPM-GPG-KEY-mysql-2022");
    private static readonly Uri Key2025Uri = new("https://repo.mysql.com/RPM-GPG-KEY-mysql-2025");
    private static readonly IReadOnlySet<string> TrustedFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "859BE8D7C586F538430B19C2467B942D3A79BD29",
        "BCA43417C3B485DD128EC6D4B7B3B788A8D3785C",
    };

    private static readonly RuntimeRelease[] SupportedReleases =
    [
        CreateRelease("8.4.11", "8.4", isLongTermSupport: true),
        CreateRelease("5.7.44", "5.7", isLongTermSupport: false),
    ];

    private readonly HttpClient _client;
    private readonly IDownloadService _downloads;
    private readonly ISignatureVerificationService _signatures;
    private readonly IInstallationLayout _layout;
    private readonly ProcessRunner _processRunner;
    private readonly MySqlPrerequisiteInstaller _prerequisites;

    public MySqlRuntimeProvider(
        HttpClient client,
        IDownloadService downloads,
        ISignatureVerificationService signatures,
        IInstallationLayout layout,
        ProcessRunner processRunner,
        MySqlPrerequisiteInstaller prerequisites)
    {
        _client = client;
        _downloads = downloads;
        _signatures = signatures;
        _layout = layout;
        _processRunner = processRunner;
        _prerequisites = prerequisites;
    }

    public RuntimeKind Kind => RuntimeKind.MySql;

    internal static IReadOnlyList<RuntimeRelease> GetSupportedReleases() => SupportedReleases;

    public Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RuntimeRelease>>(SupportedReleases);

    public Task<RuntimeRelease> ResolveAsync(
        string exactVersion,
        CancellationToken cancellationToken = default)
    {
        var normalized = ProviderUtilities.NormalizeVersion(exactVersion);
        var release = SupportedReleases.FirstOrDefault(item =>
            string.Equals(item.Version, normalized, StringComparison.Ordinal));
        return Task.FromResult(release ?? throw new RuntimeNotFoundException(Kind, exactVersion));
    }

    public async Task PrepareAsync(
        RuntimeRelease release,
        string stagingDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        var fileName = Path.GetFileName(release.DownloadUri.LocalPath);
        var archivePath = Path.Combine(_layout.DownloadsDirectory, fileName);
        var signaturePath = archivePath + ".asc";
        var keys = await LoadTrustedKeysAsync(cancellationToken);

        progress?.Report(new OperationProgress("metadata", null, "验证 MySQL 官方 OpenPGP 签名"));
        if (!File.Exists(archivePath) || !File.Exists(signaturePath))
        {
            await DownloadOfficialAssetAsync(
                release.DownloadUri,
                archivePath,
                progress,
                cancellationToken);
            await DownloadOfficialAssetAsync(
                release.SignatureUri!,
                signaturePath,
                progress: null,
                cancellationToken);
        }
        else
        {
            progress?.Report(new OperationProgress("cache", null, $"使用下载缓存：{fileName}"));
        }

        await _signatures.VerifyDetachedSignatureAsync(
            archivePath,
            signaturePath,
            keys,
            TrustedFingerprints,
            cancellationToken);

        await _prerequisites.EnsureInstalledAsync(progress, cancellationToken);

        progress?.Report(new OperationProgress("extract", null, fileName));
        SafeZipExtractor.Extract(archivePath, stagingDirectory, stripSingleRootDirectory: true);
    }

    private async Task DownloadOfficialAssetAsync(
        Uri source,
        string destinationPath,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedArchiveUri(source))
        {
            throw new IntegrityException("MySQL 下载回退仅允许 Oracle CDN 的 HTTPS 地址。");
        }

        try
        {
            await _downloads.DownloadAsync(
                source,
                destinationPath,
                progress: progress,
                cancellationToken: cancellationToken);
            return;
        }
        catch (HttpRequestException httpException)
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var curl = Path.Combine(systemDirectory, "curl.exe");
            if (!File.Exists(curl))
            {
                throw new SoftPilotException(
                    $"Oracle CDN 的 .NET TLS 连接失败，且未找到 Windows 系统 curl.exe：{GetInnermostMessage(httpException)}",
                    httpException);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            var partialPath = destinationPath + $".{Guid.NewGuid():N}.partial";
            try
            {
                progress?.Report(new OperationProgress(
                    "download-fallback",
                    null,
                    "Oracle CDN 与 .NET TLS 连接不兼容，正在改用 Windows HTTPS 下载器"));
                var result = await _processRunner.RunAsync(
                    curl,
                    BuildCurlArguments(source, partialPath),
                    cancellationToken: cancellationToken);
                if (result.ExitCode != 0 || !File.Exists(partialPath) || new FileInfo(partialPath).Length == 0)
                {
                    throw new SoftPilotException(
                        $"Oracle CDN 下载失败。.NET TLS：{GetInnermostMessage(httpException)}；Windows curl：{result.CombinedOutput}",
                        httpException);
                }

                File.Move(partialPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }
        }
    }

    internal static string[] BuildCurlArguments(Uri source, string destinationPath) =>
    [
        "--fail",
        "--silent",
        "--show-error",
        "--location",
        "--proto",
        "=https",
        "--proto-redir",
        "=https",
        "--connect-timeout",
        "20",
        "--retry",
        "2",
        "--retry-all-errors",
        "--user-agent",
        "SoftPilot/1.0 (+https://github.com/TorbenXiong/soft-pilot)",
        "--output",
        destinationPath,
        source.AbsoluteUri,
    ];

    private static string GetInnermostMessage(Exception exception)
    {
        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception.Message;
    }

    public async Task<RuntimeHealth> CheckHealthAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!_prerequisites.IsInstalled())
        {
            return new RuntimeHealth(
                false,
                null,
                $"缺少 MySQL 所需的 Microsoft Visual C++ x64 Runtime {MySqlPrerequisiteInstaller.MinimumVersion} 或更高版本。请重新执行 MySQL 安装以自动补齐。");
        }

        var server = Path.Combine(runtimeDirectory, "bin", "mysqld.exe");
        var client = Path.Combine(runtimeDirectory, "bin", "mysql.exe");
        var admin = Path.Combine(runtimeDirectory, "bin", "mysqladmin.exe");
        if (!File.Exists(server) || !File.Exists(client) || !File.Exists(admin))
        {
            return new RuntimeHealth(false, null, "MySQL 运行时缺少 mysqld.exe、mysql.exe 或 mysqladmin.exe。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var serverResult = await _processRunner.RunAsync(server, ["--version"], cancellationToken: timeout.Token);
            var match = VersionPattern().Match(serverResult.CombinedOutput);
            if (serverResult.ExitCode != 0 || !match.Success)
            {
                var detail = serverResult.CombinedOutput.Length == 0
                    ? "mysqld 无法运行；Visual C++ Runtime 可能尚未生效，请重启 Windows 后重试。"
                    : serverResult.CombinedOutput;
                return new RuntimeHealth(false, null, detail);
            }

            var clientResult = await _processRunner.RunAsync(client, ["--version"], cancellationToken: timeout.Token);
            return clientResult.ExitCode == 0
                ? new RuntimeHealth(true, match.Groups[1].Value)
                : new RuntimeHealth(false, null, $"mysql 客户端健康检查失败：{clientResult.CombinedOutput}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeHealth(false, null, "MySQL 健康检查超时（30 秒）。");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new RuntimeHealth(
                false,
                null,
                $"MySQL 可执行文件无法启动：{exception.Message} Visual C++ Runtime 可能尚未生效，请重启 Windows 后重试。");
        }
    }

    private static RuntimeRelease CreateRelease(string version, string line, bool isLongTermSupport)
    {
        var archive = new Uri($"https://cdn.mysql.com/Downloads/MySQL-{line}/mysql-{version}-winx64.zip");
        return new RuntimeRelease(
            RuntimeKind.MySql,
            version,
            RuntimeArchitecture.X64,
            archive,
            Sha256: null,
            SignatureUri: new Uri(archive.AbsoluteUri + ".asc"),
            IsLongTermSupport: isLongTermSupport,
            ReleasePageUri: new Uri($"https://dev.mysql.com/doc/relnotes/mysql/{line}/en/news-{version.Replace('.', '-')}.html"));
    }

    private async Task<string> LoadTrustedKeysAsync(CancellationToken cancellationToken)
    {
        var keys = new StringBuilder();
        foreach (var uri in new[] { Key2022Uri, Key2025Uri })
        {
            try
            {
                keys.AppendLine(await ProviderUtilities.GetRequiredStringAsync(_client, uri, cancellationToken));
            }
            catch (HttpRequestException)
            {
                // A temporarily unavailable key endpoint must not relax fingerprint validation.
            }
        }

        return keys.Length > 0
            ? keys.ToString()
            : throw new IntegrityException("无法从 MySQL 官方仓库取得受信任发布公钥。");
    }

    private static void ValidateRelease(RuntimeRelease release)
    {
        if (release.Kind != RuntimeKind.MySql
            || release.SignatureUri is null
            || !IsTrustedArchiveUri(release.DownloadUri)
            || !IsTrustedArchiveUri(release.SignatureUri)
            || !string.Equals(release.SignatureUri.AbsoluteUri, release.DownloadUri.AbsoluteUri + ".asc", StringComparison.Ordinal))
        {
            throw new IntegrityException("MySQL 下载地址或签名地址不属于受信任的 Oracle CDN。");
        }
    }

    private static bool IsTrustedArchiveUri(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "cdn.mysql.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/Downloads/MySQL-", StringComparison.Ordinal);

    [GeneratedRegex(@"\bVer\s+(\d+\.\d+\.\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
