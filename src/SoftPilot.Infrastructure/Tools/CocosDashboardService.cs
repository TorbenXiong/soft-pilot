using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Providers;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Infrastructure.Tools;

public sealed partial class CocosDashboardService : ICocosDashboardService
{
    internal static readonly Uri ReleasePageUri = new("https://www.cocos.com/en/creator-download");
    private static readonly IReadOnlyDictionary<string, string> TrustedInstallerSha256 =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CocosDashboard-v2.2.1-win-112616.exe"] =
                "f47e0bfc5bccd452361160784c6062b0b30b9933854ebc04346b8322aee3d9aa",
        };

    private readonly HttpClient _client;
    private readonly IDownloadService _downloads;
    private readonly IDownloadService _officialCdnFallbackDownloads;
    private readonly IInstallationLayout _layout;
    private readonly IStateStore _stateStore;
    private readonly ICocosDashboardSystem _system;
    private readonly string _userProfileDirectory;
    private readonly string _userDataDirectory;
    private readonly WorkspaceOperationLock _workspaceLock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CocosDashboardService(
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
            new WindowsCocosDashboardSystem(processRunner),
            CreateOfficialCdnFallbackDownloadService(),
            userProfileDirectory: null)
    {
    }

    internal CocosDashboardService(
        HttpClient client,
        IDownloadService downloads,
        IInstallationLayout layout,
        IStateStore stateStore,
        ICocosDashboardSystem system)
        : this(client, downloads, layout, stateStore, system, downloads, userProfileDirectory: null)
    {
    }

    internal CocosDashboardService(
        HttpClient client,
        IDownloadService downloads,
        IInstallationLayout layout,
        IStateStore stateStore,
        ICocosDashboardSystem system,
        IDownloadService officialCdnFallbackDownloads,
        string? userProfileDirectory = null)
    {
        _client = client;
        _downloads = downloads;
        _officialCdnFallbackDownloads = officialCdnFallbackDownloads;
        _layout = layout;
        _stateStore = stateStore;
        _system = system;
        var userProfile = userProfileDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new SoftPilotException("无法确定当前 Windows 用户目录，Cocos Dashboard 服务未启动。");
        }

        _userProfileDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile));
        _userDataDirectory = Path.Combine(
            _userProfileDirectory,
            ".Cocos");
        _workspaceLock = new WorkspaceOperationLock(layout);
    }

    public async Task<CocosDashboardInstallationStatus> GetInstalledStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installDirectory = _layout.CocosDirectory;
        var launcherPath = Path.Combine(installDirectory, "CocosDashboard.exe");
        if (!Directory.Exists(installDirectory))
        {
            return new CocosDashboardInstallationStatus(
                false,
                null,
                installDirectory,
                launcherPath);
        }

        return await CheckInstallationAsync(installDirectory, cancellationToken);
    }

    public async Task<CocosDashboardRelease> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        var html = await ProviderUtilities.GetRequiredStringAsync(
            _client,
            ReleasePageUri,
            cancellationToken);
        return ParseLatestRelease(html);
    }

    public async Task<CocosDashboardInstallationStatus> InstallOrUpgradeLatestAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var release = await GetLatestReleaseAsync(cancellationToken);
        var installed = await GetInstalledStatusAsync(cancellationToken);
        CocosDashboardInstallationStatus? completedStatus = null;
        await TrackAsync(
            installed.IsInstalled ? "upgrade" : "install",
            release.Version,
            cancellationToken,
            async operationToken =>
            {
                var stagingRoot = Path.Combine(
                    _layout.StagingDirectory,
                    $"cocos-{release.Version}-{Guid.NewGuid():N}");
                var packageDirectory = Path.Combine(stagingRoot, "package");
                var payloadDirectory = Path.Combine(stagingRoot, "payload");
                var backupDirectory = Path.Combine(
                    _layout.StagingDirectory,
                    $"cocos-backup-{Guid.NewGuid():N}");
                var installerPath = Path.Combine(_layout.DownloadsDirectory, release.AssetName);
                var previousMoved = false;
                var newVersionCommitted = false;
                try
                {
                    progress?.Report(new OperationProgress(
                        "download",
                        5,
                        "正在从 Cocos 官方来源下载安装程序"));
                    try
                    {
                        await _downloads.DownloadAsync(
                            release.DownloadUri,
                            installerPath,
                            expectedSha256: release.Sha256,
                            progress: progress,
                            cancellationToken: operationToken);
                    }
                    catch (HttpRequestException exception)
                        when (exception.StatusCode == HttpStatusCode.Forbidden)
                    {
                        progress?.Report(new OperationProgress(
                            "source",
                            null,
                            "当前 Cocos CDN 节点拒绝访问，正在切换同一官方域名的备用 CDN 连接"));
                        await _officialCdnFallbackDownloads.DownloadAsync(
                            release.DownloadUri,
                            installerPath,
                            expectedSha256: release.Sha256,
                            progress: progress,
                            cancellationToken: operationToken);
                    }

                    progress?.Report(new OperationProgress(
                        "verify",
                        73,
                        "正在验证 Cocos 安装包签名"));
                    var installerSignature = await _system.VerifyAuthenticodeAsync(
                        installerPath,
                        operationToken);
                    EnsureTrustedSignature(installerSignature, "Cocos Dashboard 安装程序");

                    Directory.CreateDirectory(packageDirectory);
                    Directory.CreateDirectory(payloadDirectory);
                    progress?.Report(new OperationProgress(
                        "extract",
                        80,
                        "正在解包 Cocos Dashboard 到独立暂存目录"));
                    await _system.ExtractPortableAsync(
                        installerPath,
                        packageDirectory,
                        payloadDirectory,
                        operationToken);

                    progress?.Report(new OperationProgress(
                        "health",
                        91,
                        "正在核对 Cocos Dashboard 实际版本与签名"));
                    var stagedStatus = await CheckInstallationAsync(payloadDirectory, operationToken);
                    if (stagedStatus.Problem is not null
                        || stagedStatus.Version is null
                        || !VersionsEquivalent(release.Version, stagedStatus.Version))
                    {
                        throw new SoftPilotException(
                            $"Cocos Dashboard 解包后健康检查失败：{stagedStatus.Problem ?? "实际版本不匹配。"}");
                    }

                    progress?.Report(new OperationProgress(
                        "commit",
                        96,
                        "正在提交 Cocos Dashboard 受管目录"));
                    Directory.CreateDirectory(Path.GetDirectoryName(_layout.CocosDirectory)!);
                    if (Directory.Exists(_layout.CocosDirectory))
                    {
                        await MoveDirectoryWithRetryAsync(
                            _layout.CocosDirectory,
                            backupDirectory,
                            operationToken);
                        previousMoved = true;
                    }

                    await MoveDirectoryWithRetryAsync(
                        payloadDirectory,
                        _layout.CocosDirectory,
                        operationToken);
                    newVersionCommitted = true;

                    completedStatus = await GetInstalledStatusAsync(operationToken);
                    if (completedStatus.Problem is not null
                        || completedStatus.Version is null
                        || !VersionsEquivalent(release.Version, completedStatus.Version))
                    {
                        throw new SoftPilotException(
                            $"Cocos Dashboard 提交后健康检查失败：{completedStatus.Problem ?? "实际版本不匹配。"}");
                    }

                    if (previousMoved)
                    {
                        TryDeleteDirectory(backupDirectory);
                        previousMoved = false;
                    }

                    progress?.Report(new OperationProgress("complete", 100, "Cocos Dashboard 已就绪"));
                }
                catch
                {
                    if (newVersionCommitted && Directory.Exists(_layout.CocosDirectory))
                    {
                        var failedDirectory = Path.Combine(stagingRoot, "failed");
                        await MoveDirectoryWithRetryAsync(
                            _layout.CocosDirectory,
                            failedDirectory,
                            CancellationToken.None);
                        newVersionCommitted = false;
                    }

                    if (previousMoved
                        && !Directory.Exists(_layout.CocosDirectory)
                        && Directory.Exists(backupDirectory))
                    {
                        await MoveDirectoryWithRetryAsync(
                            backupDirectory,
                            _layout.CocosDirectory,
                            CancellationToken.None);
                    }

                    throw;
                }
                finally
                {
                    TryDeleteDirectory(stagingRoot);
                    TryDeleteDirectory(backupDirectory);
                }
            });

        return completedStatus
            ?? throw new SoftPilotException("Cocos Dashboard 安装完成，但未生成健康检查结果。");
    }

    public Task UninstallAsync(CancellationToken cancellationToken = default) =>
        UninstallAsync(new CocosDashboardUninstallOptions(), cancellationToken);

    public async Task UninstallAsync(
        CocosDashboardUninstallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var status = await GetInstalledStatusAsync(cancellationToken);
        if (!status.IsInstalled)
        {
            return;
        }

        await TrackAsync(
            "uninstall",
            status.Version,
            cancellationToken,
            async operationToken =>
            {
                var removalPaths = new List<string> { _layout.CocosDirectory };
                if (options.DeleteData && Directory.Exists(_userDataDirectory))
                {
                    EnsureUserDataDirectoryIsSafe();
                    removalPaths.Add(_userDataDirectory);
                }

                removalPaths.AddRange(FindInstallerCachePaths());
                WindowsRemovalSafety.EnsurePathsAreDeletable(removalPaths, operationToken);
                var transactionId = Guid.NewGuid().ToString("N");
                var removalDirectory = Path.Combine(
                    _layout.StagingDirectory,
                    $"cocos-uninstall-{transactionId}");
                var removalDirectories = new List<string> { removalDirectory };
                var movedDirectories = new List<(string Original, string Staged)>();
                var movedFiles = new List<(string Original, string Staged)>();
                Directory.CreateDirectory(removalDirectory);
                try
                {
                    await MoveForUninstallAsync(
                        _layout.CocosDirectory,
                        Path.Combine(removalDirectory, "app"),
                        movedDirectories,
                        operationToken);
                    if (options.DeleteData && Directory.Exists(_userDataDirectory))
                    {
                        EnsureUserDataDirectoryIsSafe();
                        var userDataRemovalDirectory = GetRemovalDirectoryForSource(
                            _userDataDirectory,
                            removalDirectory,
                            $".softpilot-cocos-uninstall-{transactionId}");
                        if (!string.Equals(
                                userDataRemovalDirectory,
                                removalDirectory,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            removalDirectories.Add(userDataRemovalDirectory);
                        }

                        await MoveForUninstallAsync(
                            _userDataDirectory,
                            Path.Combine(userDataRemovalDirectory, "user-data"),
                            movedDirectories,
                            operationToken);
                    }

                    var cacheDirectory = Path.Combine(removalDirectory, "cache");
                    var cacheIndex = 0;
                    foreach (var cachePath in FindInstallerCachePaths())
                    {
                        Directory.CreateDirectory(cacheDirectory);
                        var staged = Path.Combine(
                            cacheDirectory,
                            $"{cacheIndex++:D3}-{Path.GetFileName(cachePath)}");
                        File.Move(cachePath, staged);
                        movedFiles.Add((cachePath, staged));
                    }

                    foreach (var directory in removalDirectories)
                    {
                        if (Directory.Exists(directory))
                        {
                            await DeleteDirectoryWithRetryAsync(directory, operationToken);
                        }
                    }
                }
                catch (Exception operationException)
                {
                    var rollbackFailures = new List<Exception>();
                    for (var index = movedDirectories.Count - 1; index >= 0; index--)
                    {
                        var (original, staged) = movedDirectories[index];
                        if (!Directory.Exists(staged) || Directory.Exists(original))
                        {
                            continue;
                        }

                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                            await MoveDirectoryWithRetryAsync(staged, original, CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            rollbackFailures.Add(exception);
                        }
                    }

                    for (var index = movedFiles.Count - 1; index >= 0; index--)
                    {
                        var (original, staged) = movedFiles[index];
                        if (!File.Exists(staged) || File.Exists(original))
                        {
                            continue;
                        }

                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                            File.Move(staged, original);
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
                            "Cocos Dashboard 卸载失败，且未能完整恢复程序、用户数据或缓存。请保留 staging 内容并检查占用进程。",
                            new AggregateException([operationException, .. rollbackFailures]));
                    }

                    foreach (var directory in removalDirectories)
                    {
                        TryDeleteDirectory(directory);
                    }

                    throw;
                }
            });
    }

    internal static string GetRemovalDirectoryForSource(
        string sourceDirectory,
        string preferredRemovalDirectory,
        string alternateDirectoryName)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var preferred = Path.GetFullPath(preferredRemovalDirectory);
        if (string.Equals(
                Path.GetPathRoot(source),
                Path.GetPathRoot(preferred),
                StringComparison.OrdinalIgnoreCase))
        {
            return preferred;
        }

        var sourceParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source));
        if (string.IsNullOrWhiteSpace(sourceParent))
        {
            throw new SoftPilotException($"无法为卸载目标创建同卷暂存目录：{sourceDirectory}");
        }

        return Path.Combine(sourceParent, alternateDirectoryName);
    }

    private async Task MoveForUninstallAsync(
        string original,
        string staged,
        ICollection<(string Original, string Staged)> movedDirectories,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(original))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await MoveDirectoryWithRetryAsync(original, staged, cancellationToken);
        movedDirectories.Add((original, staged));
    }

    private IReadOnlyList<string> FindInstallerCachePaths()
    {
        if (!Directory.Exists(_layout.DownloadsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(
                _layout.DownloadsDirectory,
                "CocosDashboard-*.exe",
                SearchOption.TopDirectoryOnly)
            .Where(path => TrustedInstallerSha256.ContainsKey(Path.GetFileName(path)))
            .ToArray();
    }

    private void EnsureUserDataDirectoryIsSafe()
    {
        var expectedName = Path.GetFileName(_userDataDirectory);
        if (!string.Equals(expectedName, ".Cocos", StringComparison.Ordinal)
            || !string.Equals(
                Path.GetDirectoryName(_userDataDirectory),
                _userProfileDirectory,
                StringComparison.OrdinalIgnoreCase)
            || new DirectoryInfo(_userDataDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new SoftPilotException(
                $"拒绝删除无法安全确认的 Cocos Dashboard 用户数据目录：{_userDataDirectory}");
        }
    }

    internal static CocosDashboardRelease ParseLatestRelease(string html)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        var normalized = html
            .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("\\/", "/", StringComparison.Ordinal);
        var releases = DashboardDownloadPattern()
            .Matches(normalized)
            .Select(match => CreateRelease(match.Value))
            .DistinctBy(release => release.DownloadUri.AbsoluteUri, StringComparer.Ordinal)
            .OrderByDescending(release => Version.Parse(release.Version))
            .ThenByDescending(release => release.AssetName, StringComparer.Ordinal)
            .ToArray();
        var latest = releases.FirstOrDefault()
            ?? throw new IntegrityException("Cocos 官方下载页缺少可信的 Windows x64 Dashboard 安装包。");
        if (string.IsNullOrEmpty(latest.Sha256))
        {
            throw new IntegrityException(
                $"Cocos Dashboard {latest.Version} 尚未包含在 SoftPilot 的可信 SHA-256 目录中，请先更新 SoftPilot。");
        }

        return latest;
    }

    internal static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionPattern().Match(value);
        return match.Success ? match.Groups["version"].Value : null;
    }

    internal static bool IsCocosPublisher(string? subject)
    {
        var normalized = string.Concat(
            (subject ?? string.Empty).Where(character =>
                !char.IsWhiteSpace(character) && character != '"'));
        return normalized.Contains(
                   "CN=XiamenYajiSoftwareCo.,Ltd.",
                   StringComparison.OrdinalIgnoreCase)
               && normalized.Contains(
                   "O=XiamenYajiSoftwareCo.,Ltd.",
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CocosDashboardInstallationStatus> CheckInstallationAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var launcherPath = Path.Combine(installDirectory, "CocosDashboard.exe");
        if (!File.Exists(launcherPath))
        {
            return new CocosDashboardInstallationStatus(
                true,
                null,
                installDirectory,
                launcherPath,
                "Cocos Dashboard 受管目录不完整，缺少 CocosDashboard.exe。");
        }

        try
        {
            var version = NormalizeVersion(_system.GetProductVersion(launcherPath));
            if (version is null)
            {
                return new CocosDashboardInstallationStatus(
                    true,
                    null,
                    installDirectory,
                    launcherPath,
                    "无法读取 Cocos Dashboard 的实际版本。");
            }

            var launcherSignature = await _system.VerifyAuthenticodeAsync(
                launcherPath,
                cancellationToken);
            if (!launcherSignature.IsValid || !IsCocosPublisher(launcherSignature.Subject))
            {
                return new CocosDashboardInstallationStatus(
                    true,
                    version,
                    installDirectory,
                    launcherPath,
                    "CocosDashboard.exe 的 Authenticode 签名无效或发布者不是 Cocos，已禁止启动。");
            }

            return new CocosDashboardInstallationStatus(
                true,
                version,
                installDirectory,
                launcherPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or JsonException)
        {
            return new CocosDashboardInstallationStatus(
                true,
                null,
                installDirectory,
                launcherPath,
                $"Cocos Dashboard 健康检查失败：{exception.Message}");
        }
    }

    private static CocosDashboardRelease CreateRelease(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var downloadUri)
            || !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(downloadUri.Host, "download.cocos.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrityException("Cocos Dashboard 下载地址不是可信的官方 HTTPS 地址。");
        }

        var pathMatch = TrustedPathPattern().Match(downloadUri.AbsolutePath);
        if (!pathMatch.Success
            || !string.Equals(
                pathMatch.Groups["directoryVersion"].Value,
                pathMatch.Groups["assetVersion"].Value,
                StringComparison.Ordinal))
        {
            throw new IntegrityException("Cocos Dashboard 下载地址中的版本或资产名称无效。");
        }

        var assetName = pathMatch.Groups["asset"].Value;
        TrustedInstallerSha256.TryGetValue(assetName, out var sha256);
        return new CocosDashboardRelease(
            pathMatch.Groups["assetVersion"].Value,
            downloadUri,
            ReleasePageUri,
            assetName,
            sha256 ?? string.Empty);
    }

    private static void EnsureTrustedSignature(CocosAuthenticodeVerification signature, string fileName)
    {
        if (!signature.IsValid || !IsCocosPublisher(signature.Subject))
        {
            throw new IntegrityException(
                $"{fileName} 的 Authenticode 签名无效或发布者不是 Cocos（{signature.Subject ?? signature.Problem ?? "无签名"}）。");
        }
    }

    private static bool VersionsEquivalent(string expected, string actual) =>
        string.Equals(NormalizeVersion(expected), NormalizeVersion(actual), StringComparison.OrdinalIgnoreCase);

    private static IDownloadService CreateOfficialCdnFallbackDownloadService()
    {
        const string officialHost = "download.cocos.com";
        const string officialCdnConnectionHost = "download.cocos.com.wtxcdn.com";
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(
                        context.DnsEndPoint.Host,
                        officialHost,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException(
                        $"Cocos 备用 CDN 连接拒绝非官方主机：{context.DnsEndPoint.Host}");
                }

                var addresses = await Dns.GetHostAddressesAsync(
                    officialCdnConnectionHost,
                    cancellationToken);
                Exception? firstFailure = null;
                foreach (var address in addresses.OrderBy(value =>
                             value.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(address, context.DnsEndPoint.Port),
                            cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException exception)
                    {
                        firstFailure ??= exception;
                        socket.Dispose();
                    }
                }

                throw new HttpRequestException(
                    "无法连接 Cocos 官方域名的备用 CDN 节点。",
                    firstFailure);
            },
        };
        return new HttpDownloadService(new HttpClient(handler));
    }

    private async Task TrackAsync(
        string name,
        string? version,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action)
    {
        await using var workspaceLease = await _workspaceLock.AcquireAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        var operation = new OperationRecord(
            Guid.NewGuid(),
            $"cocos-{name}",
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
            // Staging cleanup is best-effort; the original operation result remains authoritative.
        }
    }

    [GeneratedRegex(
        "https://download\\.cocos\\.com/CocosDashboard/v\\d+\\.\\d+\\.\\d+/CocosDashboard-v\\d+\\.\\d+\\.\\d+-win(?:32)?-\\d+\\.exe",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DashboardDownloadPattern();

    [GeneratedRegex(
        "^/CocosDashboard/v(?<directoryVersion>\\d+\\.\\d+\\.\\d+)/(?<asset>CocosDashboard-v(?<assetVersion>\\d+\\.\\d+\\.\\d+)-win(?:32)?-\\d+\\.exe)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TrustedPathPattern();

    [GeneratedRegex("(?<!\\d)(?<version>\\d+\\.\\d+\\.\\d+)(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

internal interface ICocosDashboardSystem
{
    string? GetProductVersion(string launcherPath);

    Task<CocosAuthenticodeVerification> VerifyAuthenticodeAsync(
        string path,
        CancellationToken cancellationToken);

    Task ExtractPortableAsync(
        string installerPath,
        string packageDirectory,
        string payloadDirectory,
        CancellationToken cancellationToken);
}

internal sealed record CocosAuthenticodeVerification(
    bool IsValid,
    string? Subject,
    string? Problem = null);

internal sealed class WindowsCocosDashboardSystem(ProcessRunner processRunner) : ICocosDashboardSystem
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string? GetProductVersion(string launcherPath) =>
        FileVersionInfo.GetVersionInfo(launcherPath).ProductVersion;

    public async Task<CocosAuthenticodeVerification> VerifyAuthenticodeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $securityModule = Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1'
            Import-Module $securityModule -Force
            $path = [Environment]::GetEnvironmentVariable('SOFTPILOT_COCOS_VERIFY_PATH')
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            [pscustomobject]@{
                IsValid = $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid
                Subject = $signature.SignerCertificate.Subject
            } | ConvertTo-Json -Compress
            """;
        var result = await processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            environment: new Dictionary<string, string?> { ["SOFTPILOT_COCOS_VERIFY_PATH"] = path },
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            return new CocosAuthenticodeVerification(
                false,
                null,
                string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? "Windows PowerShell 签名验证失败"
                    : result.CombinedOutput);
        }

        return JsonSerializer.Deserialize<CocosAuthenticodeVerification>(result.StandardOutput, JsonOptions)
            ?? new CocosAuthenticodeVerification(false, null);
    }

    public async Task ExtractPortableAsync(
        string installerPath,
        string packageDirectory,
        string payloadDirectory,
        CancellationToken cancellationToken)
    {
        var extraction = await processRunner.RunAsync(
            installerPath,
            ["/extract", packageDirectory],
            packageDirectory,
            cancellationToken: cancellationToken);
        if (extraction.ExitCode != 0)
        {
            throw new SoftPilotException(
                $"Cocos Dashboard 安装包提取失败（退出码 {extraction.ExitCode}）：{extraction.CombinedOutput}");
        }

        var packages = Directory
            .EnumerateFiles(packageDirectory, "*.msi", SearchOption.AllDirectories)
            .ToArray();
        if (packages.Length != 1)
        {
            throw new IntegrityException(
                $"Cocos Dashboard 安装包应只包含一个 MSI，实际发现 {packages.Length} 个。");
        }

        var installer = await processRunner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            ["/a", packages[0], "/qn", "/norestart", $"TARGETDIR={payloadDirectory}"],
            packageDirectory,
            cancellationToken: cancellationToken);
        if (installer.ExitCode != 0)
        {
            throw new SoftPilotException(
                $"Cocos Dashboard 便携文件提取失败（退出码 {installer.ExitCode}）：{installer.CombinedOutput}");
        }

        foreach (var administrativePackage in Directory.EnumerateFiles(
                     payloadDirectory,
                     "*.msi",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(administrativePackage);
        }
    }
}
