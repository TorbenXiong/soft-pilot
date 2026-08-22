using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Providers;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Infrastructure.Tools;

public sealed partial class CocosCreatorService : ICocosCreatorService
{
    internal static readonly Uri ReleasePageUri = new("https://www.cocos.com/en/creator-download");

    private readonly HttpClient _client;
    private readonly IDownloadService _downloads;
    private readonly IInstallationLayout _layout;
    private readonly IStateStore _stateStore;
    private readonly ICocosCreatorSystem _system;
    private readonly WorkspaceOperationLock _workspaceLock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CocosCreatorService(
        HttpClient client,
        IDownloadService downloads,
        IInstallationLayout layout,
        IStateStore stateStore,
        ProcessRunner processRunner)
        : this(
            client,
            downloads,
            layout,
            stateStore,
            new WindowsCocosCreatorSystem(processRunner))
    {
    }

    internal CocosCreatorService(
        HttpClient client,
        IDownloadService downloads,
        IInstallationLayout layout,
        IStateStore stateStore,
        ICocosCreatorSystem system)
    {
        _client = client;
        _downloads = downloads;
        _layout = layout;
        _stateStore = stateStore;
        _system = system;
        _workspaceLock = new WorkspaceOperationLock(layout);
    }

    public string GetInstallDirectory(string version) =>
        _layout.GetCocosCreatorDirectory(version);

    public async Task<IReadOnlyList<CocosCreatorRelease>> GetAvailableReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        var html = await ProviderUtilities.GetRequiredStringAsync(
            _client,
            ReleasePageUri,
            cancellationToken);
        return [ParseReleases(html)[0]];
    }

    public async Task<IReadOnlyList<CocosCreatorInstallationStatus>> GetInstalledStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_layout.CocosCreatorDirectory))
        {
            return [];
        }

        var versions = Directory
            .EnumerateDirectories(_layout.CocosCreatorDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsCanonicalVersion)
            .OrderByDescending(Version.Parse)
            .ToArray();
        var statuses = new List<CocosCreatorInstallationStatus>(versions.Length);
        foreach (var version in versions)
        {
            statuses.Add(await CheckInstallationAsync(
                version,
                _layout.GetCocosCreatorDirectory(version),
                cancellationToken));
        }

        return statuses;
    }

    public Task<CocosCreatorInstallationStatus> InstallAsync(
        CocosCreatorRelease release,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        InstallOrUpgradeAsync("install", release, progress, cancellationToken);

    public Task<CocosCreatorInstallationStatus> UpgradeAsync(
        CocosCreatorRelease release,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        InstallOrUpgradeAsync("upgrade", release, progress, cancellationToken);

    private async Task<CocosCreatorInstallationStatus> InstallOrUpgradeAsync(
        string operationName,
        CocosCreatorRelease release,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);
        var finalDirectory = _layout.GetCocosCreatorDirectory(release.Version);
        EnsureDirectoryIsNotReparsePoint(_layout.CocosCreatorDirectory);
        EnsureDirectoryIsNotReparsePoint(Path.Combine(_layout.DownloadsDirectory, "cocos-creator"));
        if (Directory.Exists(finalDirectory))
        {
            var existing = await CheckInstallationAsync(
                release.Version,
                finalDirectory,
                cancellationToken);
            if (existing.IsHealthy)
            {
                return existing;
            }

            throw new SoftPilotException(
                $"Cocos Creator {release.Version} 的受管目录已存在但健康检查失败，请先卸载该版本：{existing.Problem}");
        }

        CocosCreatorInstallationStatus? completedStatus = null;
        await TrackAsync(
            operationName,
            release.Version,
            cancellationToken,
            async operationToken =>
            {
                var stagingRoot = Path.Combine(
                    _layout.StagingDirectory,
                    $"cocos-creator-{release.Version}-{Guid.NewGuid():N}");
                var payloadDirectory = Path.Combine(stagingRoot, "payload");
                var committed = false;
                var downloadDirectory = _layout.GetCocosCreatorDownloadDirectory(release.Version);
                var archivePath = Path.Combine(downloadDirectory, release.AssetName);
                var hashPath = archivePath + ".sha256";
                try
                {
                    progress?.Report(new OperationProgress(
                        "download",
                        1,
                        $"正在从 Cocos 官方来源下载 Creator {release.Version}"));
                    var download = await _downloads.DownloadAsync(
                        release.DownloadUri,
                        archivePath,
                        progress: progress,
                        cancellationToken: operationToken);
                    await File.WriteAllTextAsync(hashPath, download.Sha256 + Environment.NewLine, operationToken);

                    progress?.Report(new OperationProgress(
                        "verify",
                        75,
                        "正在复核下载缓存的 SHA-256"));
                    var actualSha256 = await _downloads.ComputeSha256Async(archivePath, operationToken);
                    EnsureMatchingSha256(download.Sha256, actualSha256);

                    progress?.Report(new OperationProgress(
                        "extract",
                        80,
                        "正在安全解包 Cocos Creator 到独立暂存目录"));
                    SafeZipExtractor.Extract(
                        archivePath,
                        payloadDirectory,
                        stripSingleRootDirectory: true);

                    progress?.Report(new OperationProgress(
                        "health",
                        92,
                        "正在核对 Cocos Creator 实际版本与官方数字签名"));
                    var stagedStatus = await CheckInstallationAsync(
                        release.Version,
                        payloadDirectory,
                        operationToken);
                    if (!stagedStatus.IsHealthy)
                    {
                        throw new SoftPilotException(
                            $"Cocos Creator {release.Version} 解包后健康检查失败：{stagedStatus.Problem}");
                    }

                    progress?.Report(new OperationProgress(
                        "commit",
                        97,
                        "正在提交 Cocos Creator 受管版本目录"));
                    Directory.CreateDirectory(_layout.CocosCreatorDirectory);
                    await MoveDirectoryWithRetryAsync(payloadDirectory, finalDirectory, operationToken);
                    committed = true;

                    completedStatus = await CheckInstallationAsync(
                        release.Version,
                        finalDirectory,
                        operationToken);
                    if (!completedStatus.IsHealthy)
                    {
                        throw new SoftPilotException(
                            $"Cocos Creator {release.Version} 提交后健康检查失败：{completedStatus.Problem}");
                    }

                    progress?.Report(new OperationProgress(
                        "complete",
                        100,
                        $"Cocos Creator {release.Version} 已就绪"));
                }
                catch
                {
                    if (committed && Directory.Exists(finalDirectory))
                    {
                        var failedDirectory = Path.Combine(stagingRoot, "failed");
                        await MoveDirectoryWithRetryAsync(
                            finalDirectory,
                            failedDirectory,
                            CancellationToken.None);
                    }

                    throw;
                }
                finally
                {
                    TryDeleteDirectory(stagingRoot);
                }
            });

        return completedStatus
            ?? throw new SoftPilotException(
                $"Cocos Creator {release.Version} 安装完成，但未生成健康检查结果。");
    }

    public async Task UninstallAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        if (!IsCanonicalVersion(version))
        {
            throw new ArgumentException($"Cocos Creator 版本号无效：{version}", nameof(version));
        }

        var installDirectory = _layout.GetCocosCreatorDirectory(version);
        var cacheDirectory = _layout.GetCocosCreatorDownloadDirectory(version);
        EnsureDirectoryIsNotReparsePoint(installDirectory);
        EnsureDirectoryIsNotReparsePoint(cacheDirectory);
        if (!Directory.Exists(installDirectory) && !Directory.Exists(cacheDirectory))
        {
            return;
        }

        await TrackAsync(
            "uninstall",
            version,
            cancellationToken,
            async operationToken =>
            {
                WindowsRemovalSafety.EnsurePathsAreDeletable(
                    [installDirectory, cacheDirectory],
                    operationToken);
                var removalDirectory = Path.Combine(
                    _layout.StagingDirectory,
                    $"cocos-creator-uninstall-{version}-{Guid.NewGuid():N}");
                var moved = new List<(string Original, string Staged)>();
                Directory.CreateDirectory(removalDirectory);
                try
                {
                    await MoveForUninstallAsync(
                        installDirectory,
                        Path.Combine(removalDirectory, "app"),
                        moved,
                        operationToken);
                    await MoveForUninstallAsync(
                        cacheDirectory,
                        Path.Combine(removalDirectory, "cache"),
                        moved,
                        operationToken);
                    await DeleteDirectoryWithRetryAsync(removalDirectory, operationToken);
                    TryDeleteEmptyDirectory(_layout.CocosCreatorDirectory);
                    TryDeleteEmptyDirectory(Path.Combine(_layout.DownloadsDirectory, "cocos-creator"));
                }
                catch (Exception operationException)
                {
                    var rollbackFailures = new List<Exception>();
                    for (var index = moved.Count - 1; index >= 0; index--)
                    {
                        var (original, staged) = moved[index];
                        if (!Directory.Exists(staged) || Directory.Exists(original))
                        {
                            continue;
                        }

                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                            await MoveDirectoryWithRetryAsync(staged, original, CancellationToken.None);
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException)
                        {
                            rollbackFailures.Add(exception);
                        }
                    }

                    if (rollbackFailures.Count > 0)
                    {
                        throw new SoftPilotException(
                            $"Cocos Creator {version} 卸载失败，且未能完整恢复编辑器或缓存。请保留 staging 内容并检查占用进程。",
                            new AggregateException([operationException, .. rollbackFailures]));
                    }

                    TryDeleteDirectory(removalDirectory);
                    throw;
                }
            });
    }

    internal static IReadOnlyList<CocosCreatorRelease> ParseReleases(string html)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        var normalized = html
            .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("\\/", "/", StringComparison.Ordinal);
        var releases = CreatorDownloadPattern()
            .Matches(normalized)
            .Select(match => CreateRelease(match.Value))
            .DistinctBy(release => release.Version, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(release => Version.Parse(release.Version))
            .ToArray();
        return releases.Length > 0
            ? releases
            : throw new IntegrityException(
                "Cocos 官方下载页缺少可信的 Windows Cocos Creator ZIP。有关版本安装已被阻止。");
    }

    internal static bool IsTrustedFinalDownloadAddress(Uri source, Uri finalAddress) =>
        string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(finalAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(source.Host, "download.cocos.com", StringComparison.OrdinalIgnoreCase)
        && string.Equals(finalAddress.Host, source.Host, StringComparison.OrdinalIgnoreCase);

    private async Task<CocosCreatorInstallationStatus> CheckInstallationAsync(
        string expectedVersion,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var launcherPath = Path.Combine(installDirectory, "CocosCreator.exe");
        if (!File.Exists(launcherPath))
        {
            return new CocosCreatorInstallationStatus(
                expectedVersion,
                installDirectory,
                launcherPath,
                "受管目录不完整，缺少 CocosCreator.exe。");
        }

        try
        {
            if (new DirectoryInfo(installDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new CocosCreatorInstallationStatus(
                    expectedVersion,
                    installDirectory,
                    launcherPath,
                    "受管版本目录是重解析点，已禁止启动或卸载。 ");
            }

            var actualVersion = CocosDashboardService.NormalizeVersion(
                _system.GetProductVersion(launcherPath));
            if (!string.Equals(expectedVersion, actualVersion, StringComparison.OrdinalIgnoreCase))
            {
                return new CocosCreatorInstallationStatus(
                    expectedVersion,
                    installDirectory,
                    launcherPath,
                    $"实际版本不匹配：期望 {expectedVersion}，检测到 {actualVersion ?? "未知"}。");
            }

            var signature = await _system.VerifyAuthenticodeAsync(launcherPath, cancellationToken);
            if (!signature.IsValid || !CocosDashboardService.IsCocosPublisher(signature.Subject))
            {
                return new CocosCreatorInstallationStatus(
                    expectedVersion,
                    installDirectory,
                    launcherPath,
                    "CocosCreator.exe 的 Authenticode 签名无效或发布者不是 Cocos，已禁止启动。");
            }

            return new CocosCreatorInstallationStatus(
                expectedVersion,
                installDirectory,
                launcherPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new CocosCreatorInstallationStatus(
                expectedVersion,
                installDirectory,
                launcherPath,
                $"Cocos Creator 健康检查失败：{exception.Message}");
        }
    }

    private static CocosCreatorRelease CreateRelease(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var downloadUri)
            || !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(downloadUri.Host, "download.cocos.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(downloadUri.Query)
            || !string.IsNullOrEmpty(downloadUri.Fragment))
        {
            throw new IntegrityException("Cocos Creator 下载地址不是可信的官方 HTTPS 地址。");
        }

        var match = TrustedPathPattern().Match(downloadUri.AbsolutePath);
        if (!match.Success
            || !string.Equals(
                match.Groups["directoryVersion"].Value,
                match.Groups["assetVersion"].Value,
                StringComparison.Ordinal))
        {
            throw new IntegrityException("Cocos Creator 下载地址中的版本或资产名称无效。");
        }

        return new CocosCreatorRelease(
            match.Groups["assetVersion"].Value,
            downloadUri,
            ReleasePageUri,
            match.Groups["asset"].Value);
    }

    private static void ValidateRelease(CocosCreatorRelease release)
    {
        var validated = CreateRelease(release.DownloadUri.AbsoluteUri);
        if (!string.Equals(validated.Version, release.Version, StringComparison.Ordinal)
            || !string.Equals(validated.AssetName, release.AssetName, StringComparison.Ordinal)
            || release.ReleasePageUri != ReleasePageUri)
        {
            throw new IntegrityException("Cocos Creator 版本记录与可信官方下载地址不一致。");
        }
    }

    private static bool IsCanonicalVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && Version.TryParse(version, out var parsed)
        && parsed.Major >= 0
        && parsed.Minor >= 0
        && parsed.Build >= 0
        && parsed.Revision < 0
        && string.Equals(parsed.ToString(3), version, StringComparison.Ordinal);

    private static void EnsureMatchingSha256(string expected, string actual)
    {
        byte[] expectedBytes;
        byte[] actualBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expected);
            actualBytes = Convert.FromHexString(actual);
        }
        catch (FormatException exception)
        {
            throw new IntegrityException($"Cocos Creator 缓存 SHA-256 无效：{exception.Message}");
        }

        if (expectedBytes.Length != 32
            || actualBytes.Length != 32
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new IntegrityException("Cocos Creator 下载缓存的 SHA-256 复核失败。");
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        if (Directory.Exists(path)
            && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new SoftPilotException($"拒绝操作重解析点形式的 Cocos Creator 受管目录：{path}");
        }
    }

    private static async Task MoveForUninstallAsync(
        string original,
        string staged,
        ICollection<(string Original, string Staged)> moved,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(original))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await MoveDirectoryWithRetryAsync(original, staged, cancellationToken);
        moved.Add((original, staged));
    }

    private async Task TrackAsync(
        string name,
        string version,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        var operation = new OperationRecord(
            Guid.NewGuid(),
            $"cocos-creator-{name}",
            null,
            version,
            OperationStatus.Running,
            DateTimeOffset.UtcNow);
        try
        {
            await _stateStore.AddOperationAsync(operation, cancellationToken);
            await action(cancellationToken);
            await _stateStore.CompleteOperationAsync(
                operation.Id,
                OperationStatus.Succeeded,
                cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await _stateStore.CompleteOperationAsync(
                operation.Id,
                OperationStatus.Cancelled,
                cancellationToken: CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await _stateStore.CompleteOperationAsync(
                operation.Id,
                OperationStatus.Failed,
                exception.Message,
                CancellationToken.None);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task MoveDirectoryWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (
                attempt < 3 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (1 << attempt)), cancellationToken);
            }
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 3 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (1 << attempt)), cancellationToken);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Staging cleanup is best-effort; the operation record remains authoritative.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Empty parent cleanup is best-effort.
        }
    }

    [GeneratedRegex(
        "https://download\\.cocos\\.com/CocosCreator/v\\d+\\.\\d+\\.\\d+/CocosCreator-v\\d+\\.\\d+\\.\\d+-win-\\d+\\.zip",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CreatorDownloadPattern();

    [GeneratedRegex(
        "^/CocosCreator/v(?<directoryVersion>\\d+\\.\\d+\\.\\d+)/(?<asset>CocosCreator-v(?<assetVersion>\\d+\\.\\d+\\.\\d+)-win-\\d+\\.zip)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TrustedPathPattern();
}

internal interface ICocosCreatorSystem
{
    string? GetProductVersion(string launcherPath);

    Task<CocosAuthenticodeVerification> VerifyAuthenticodeAsync(
        string path,
        CancellationToken cancellationToken);
}

internal sealed class WindowsCocosCreatorSystem(ProcessRunner processRunner) : ICocosCreatorSystem
{
    private readonly WindowsCocosDashboardSystem _dashboardSystem = new(processRunner);

    public string? GetProductVersion(string launcherPath) =>
        FileVersionInfo.GetVersionInfo(launcherPath).ProductVersion;

    public Task<CocosAuthenticodeVerification> VerifyAuthenticodeAsync(
        string path,
        CancellationToken cancellationToken) =>
        _dashboardSystem.VerifyAuthenticodeAsync(path, cancellationToken);
}
