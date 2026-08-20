using System.Text.Json;
using System.Text.RegularExpressions;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Providers;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Infrastructure.Tools;

public sealed partial class GitService : IGitService
{
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/git-for-windows/git/releases/latest");
    private const string TrustedDownloadPrefix = "/git-for-windows/git/releases/download/";

    private readonly HttpClient _client;
    private readonly IDownloadService _downloads;
    private readonly IInstallationLayout _layout;
    private readonly IStateStore _stateStore;
    private readonly ProcessRunner _processRunner;
    private readonly WorkspaceOperationLock _workspaceLock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitService(
        HttpClient client,
        IDownloadService downloads,
        IInstallationLayout layout,
        IStateStore stateStore,
        ProcessRunner processRunner)
    {
        _client = client;
        _downloads = downloads;
        _layout = layout;
        _stateStore = stateStore;
        _processRunner = processRunner;
        _workspaceLock = new WorkspaceOperationLock(layout);
    }

    public string InstallDirectory => _layout.GitDirectory;
    public string LauncherPath => Path.Combine(InstallDirectory, "git-bash.exe");

    public async Task<GitInstallationStatus> GetInstalledStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var git = Path.Combine(InstallDirectory, "cmd", "git.exe");
        if (!Directory.Exists(InstallDirectory))
        {
            return new GitInstallationStatus(false, null, InstallDirectory, LauncherPath);
        }

        if (!File.Exists(git) || !File.Exists(LauncherPath))
        {
            return new GitInstallationStatus(
                true,
                null,
                InstallDirectory,
                LauncherPath,
                "Git 安装目录不完整，缺少 git-bash.exe 或 cmd\\git.exe。");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await _processRunner.RunAsync(git, ["--version"], cancellationToken: timeout.Token);
            var version = ParseInstalledVersion(result.CombinedOutput);
            return result.ExitCode == 0 && version is not null
                ? new GitInstallationStatus(true, version, InstallDirectory, LauncherPath)
                : new GitInstallationStatus(
                    true,
                    null,
                    InstallDirectory,
                    LauncherPath,
                    $"Git 健康检查失败：{result.CombinedOutput}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitInstallationStatus(
                true,
                null,
                InstallDirectory,
                LauncherPath,
                "Git 健康检查超时（15 秒）。");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new GitInstallationStatus(
                true,
                null,
                InstallDirectory,
                LauncherPath,
                $"Git 健康检查失败：{exception.Message}");
        }
    }

    public async Task<IReadOnlyList<GitEnvironmentCheck>> GetEnvironmentChecksAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(InstallDirectory))
        {
            return [];
        }

        var ssh = Path.Combine(InstallDirectory, "usr", "bin", "ssh.exe");
        var gitLfs = Path.Combine(InstallDirectory, "mingw64", "bin", "git-lfs.exe");
        return
        [
            await CheckCommandAsync("SSH", ssh, ["-V"], cancellationToken),
            await CheckCommandAsync("Git LFS", gitLfs, ["version"], cancellationToken),
        ];
    }

    public async Task<GitGlobalConfiguration> GetGlobalConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var git = GetInstalledGitExecutable();
        return new GitGlobalConfiguration(
            await ReadGlobalConfigurationValueAsync(git, "user.name", cancellationToken),
            await ReadGlobalConfigurationValueAsync(git, "user.email", cancellationToken));
    }

    public async Task SaveGlobalConfigurationAsync(
        GitGlobalConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var git = GetInstalledGitExecutable();
        await WriteGlobalConfigurationValueAsync(
            git,
            "user.name",
            configuration.UserName.Trim(),
            cancellationToken);
        await WriteGlobalConfigurationValueAsync(
            git,
            "user.email",
            configuration.UserEmail.Trim(),
            cancellationToken);
    }

    public async Task<GitRelease> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await ProviderUtilities.GetRequiredStringAsync(
            _client,
            LatestReleaseUri,
            cancellationToken);
        return ParseLatestRelease(json);
    }

    public async Task<GitInstallationStatus> InstallOrUpgradeLatestAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var release = await GetLatestReleaseAsync(cancellationToken);
        var installed = await GetInstalledStatusAsync(cancellationToken);
        await TrackAsync(
            GetInstallOperationName(installed.IsInstalled),
            release.Version,
            cancellationToken,
            async operationToken =>
            {
                var stagingDirectory = Path.Combine(
                    _layout.StagingDirectory,
                    $"git-{release.Version}-{Guid.NewGuid():N}");
                var backupDirectory = Path.Combine(
                    _layout.StagingDirectory,
                    $"git-backup-{Guid.NewGuid():N}");
                var archivePath = Path.Combine(_layout.DownloadsDirectory, release.AssetName);
                var previousMoved = false;
                try
                {
                    progress?.Report(new OperationProgress("download", 5, "正在下载 Git for Windows 便携版"));
                    await _downloads.DownloadAsync(
                        release.DownloadUri,
                        archivePath,
                        release.Sha256,
                        progress,
                        operationToken);

                    Directory.CreateDirectory(stagingDirectory);
                    progress?.Report(new OperationProgress("extract", 72, "正在解包 Git"));
                    var extraction = await _processRunner.RunAsync(
                        archivePath,
                        ["-y", $"-o{stagingDirectory}"],
                        _layout.StagingDirectory,
                        cancellationToken: operationToken);
                    if (extraction.ExitCode != 0)
                    {
                        throw new SoftPilotException($"Git 归档解包失败：{extraction.CombinedOutput}");
                    }

                    progress?.Report(new OperationProgress("health", 86, "正在核对 Git 实际版本"));
                    var detectedVersion = await CheckVersionAsync(stagingDirectory, operationToken);
                    if (!AreEquivalent(release.Version, detectedVersion))
                    {
                        throw new IntegrityException(
                            $"Git 发布版本 {release.Version} 与实际版本 {detectedVersion} 不一致。");
                    }

                    progress?.Report(new OperationProgress("commit", 94, "正在提交 Git 目录"));
                    Directory.CreateDirectory(Path.GetDirectoryName(InstallDirectory)!);
                    if (Directory.Exists(InstallDirectory))
                    {
                        await MoveDirectoryWithRetryAsync(InstallDirectory, backupDirectory, operationToken);
                        previousMoved = true;
                    }

                    await MoveDirectoryWithRetryAsync(stagingDirectory, InstallDirectory, operationToken);
                    if (previousMoved)
                    {
                        TryDeleteDirectory(backupDirectory);
                        previousMoved = false;
                    }

                    progress?.Report(new OperationProgress("complete", 100, "Git 已就绪"));
                }
                catch
                {
                    if (previousMoved && !Directory.Exists(InstallDirectory) && Directory.Exists(backupDirectory))
                    {
                        await MoveDirectoryWithRetryAsync(
                            backupDirectory,
                            InstallDirectory,
                            CancellationToken.None);
                    }

                    throw;
                }
                finally
                {
                    TryDeleteDirectory(stagingDirectory);
                    TryDeleteDirectory(backupDirectory);
                }
            });

        return await GetInstalledStatusAsync(cancellationToken);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
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
                var stagingDirectory = Path.Combine(
                    _layout.StagingDirectory,
                    $"git-uninstall-{Guid.NewGuid():N}");
                await MoveDirectoryWithRetryAsync(InstallDirectory, stagingDirectory, operationToken);
                try
                {
                    await DeleteDirectoryWithRetryAsync(stagingDirectory, operationToken);
                }
                catch
                {
                    if (!Directory.Exists(InstallDirectory) && Directory.Exists(stagingDirectory))
                    {
                        await MoveDirectoryWithRetryAsync(
                            stagingDirectory,
                            InstallDirectory,
                            CancellationToken.None);
                    }

                    throw;
                }
            });
    }

    internal static GitRelease ParseLatestRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
        {
            throw new IntegrityException("Git for Windows 最新发布记录不是稳定版本。");
        }

        var tag = ProviderUtilities.ReadFlexibleString(root, "tag_name") ?? string.Empty;
        var tagMatch = StableTagPattern().Match(tag);
        var releasePage = ProviderUtilities.ReadFlexibleString(root, "html_url");
        if (!tagMatch.Success
            || !Uri.TryCreate(releasePage, UriKind.Absolute, out var releasePageUri)
            || !IsTrustedReleasePage(releasePageUri))
        {
            throw new IntegrityException("Git for Windows 最新发布记录包含无效的版本或发布页。");
        }

        var version = tagMatch.Groups["version"].Value + ".windows." + tagMatch.Groups["revision"].Value;
        var assetVersion = tagMatch.Groups["version"].Value + "." + tagMatch.Groups["revision"].Value;
        var expectedName = $"PortableGit-{assetVersion}-64-bit.7z.exe";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new IntegrityException("Git for Windows 最新发布记录缺少资产列表。");
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
                || !IsTrustedDownload(downloadUri, tag, expectedName)
                || digest is null
                || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || !Sha256Pattern().IsMatch(digest[7..]))
            {
                throw new IntegrityException("Git for Windows 便携版资产缺少可信下载地址或 SHA-256 摘要。");
            }

            return new GitRelease(
                version,
                downloadUri,
                digest[7..].ToLowerInvariant(),
                releasePageUri,
                expectedName);
        }

        throw new IntegrityException($"Git for Windows 最新发布缺少 {expectedName}。");
    }

    internal static string GetInstallOperationName(bool isInstalled) =>
        isInstalled ? "upgrade" : "install";

    internal static string? ParseInstalledVersion(string output)
    {
        var match = InstalledVersionPattern().Match(output.Trim());
        return match.Success ? match.Groups["version"].Value : null;
    }

    private async Task<GitEnvironmentCheck> CheckCommandAsync(
        string name,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            return new GitEnvironmentCheck(name, false, "Not found");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await _processRunner.RunAsync(
                executable,
                arguments,
                cancellationToken: timeout.Token);
            return new GitEnvironmentCheck(
                name,
                result.ExitCode == 0,
                string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? $"Exit code {result.ExitCode}"
                    : result.CombinedOutput);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitEnvironmentCheck(name, false, "Timed out");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new GitEnvironmentCheck(name, false, exception.Message);
        }
    }

    private string GetInstalledGitExecutable()
    {
        var git = Path.Combine(InstallDirectory, "cmd", "git.exe");
        if (!File.Exists(git))
        {
            throw new SoftPilotException("请先安装 Git，再读取或保存全局配置。");
        }

        return git;
    }

    private async Task<string> ReadGlobalConfigurationValueAsync(
        string git,
        string key,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            git,
            ["config", "--global", "--get", key],
            cancellationToken: cancellationToken);
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.CombinedOutput))
        {
            return string.Empty;
        }

        if (result.ExitCode != 0)
        {
            throw new SoftPilotException($"读取 Git 全局配置 {key} 失败：{result.CombinedOutput}");
        }

        return result.StandardOutput.Trim();
    }

    private async Task WriteGlobalConfigurationValueAsync(
        string git,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = string.IsNullOrEmpty(value)
            ? ["config", "--global", "--unset-all", key]
            : ["config", "--global", "--replace-all", key, value];
        var result = await _processRunner.RunAsync(
            git,
            arguments,
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0
            && !(string.IsNullOrEmpty(value)
                 && result.ExitCode == 5
                 && string.IsNullOrWhiteSpace(result.CombinedOutput)))
        {
            throw new SoftPilotException($"保存 Git 全局配置 {key} 失败：{result.CombinedOutput}");
        }
    }

    private async Task<string> CheckVersionAsync(string directory, CancellationToken cancellationToken)
    {
        var git = Path.Combine(directory, "cmd", "git.exe");
        var launcher = Path.Combine(directory, "git-bash.exe");
        if (!File.Exists(git) || !File.Exists(launcher))
        {
            throw new SoftPilotException("Git 健康检查失败：缺少 git-bash.exe 或 cmd\\git.exe。");
        }

        var result = await _processRunner.RunAsync(git, ["--version"], cancellationToken: cancellationToken);
        var version = ParseInstalledVersion(result.CombinedOutput);
        if (result.ExitCode != 0 || version is null)
        {
            throw new SoftPilotException($"Git 健康检查失败：{result.CombinedOutput}");
        }

        return version;
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
            name,
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

    private static bool AreEquivalent(string releaseVersion, string detectedVersion) =>
        string.Equals(releaseVersion, detectedVersion, StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            releaseVersion.Replace(".windows.", ".", StringComparison.OrdinalIgnoreCase),
            detectedVersion.Replace(".windows.", ".", StringComparison.OrdinalIgnoreCase),
            StringComparison.OrdinalIgnoreCase);

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsTrustedReleasePage(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/git-for-windows/git/releases/tag/", StringComparison.Ordinal);

    private static bool IsTrustedDownload(Uri uri, string tag, string assetName) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(TrustedDownloadPrefix, StringComparison.Ordinal)
        && string.Equals(
            uri.AbsolutePath,
            $"{TrustedDownloadPrefix}{Uri.EscapeDataString(tag)}/{assetName}",
            StringComparison.Ordinal);

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
        "^v(?<version>\\d+\\.\\d+\\.\\d+)\\.windows\\.(?<revision>\\d+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StableTagPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(
        "^git version (?<version>\\d+\\.\\d+\\.\\d+(?:\\.windows\\.\\d+|\\.\\d+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InstalledVersionPattern();

}
