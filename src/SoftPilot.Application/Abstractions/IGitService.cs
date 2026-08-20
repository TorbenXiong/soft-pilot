namespace SoftPilot.Application.Abstractions;

public interface IGitService
{
    string InstallDirectory { get; }
    string LauncherPath { get; }

    Task<GitInstallationStatus> GetInstalledStatusAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitEnvironmentCheck>> GetEnvironmentChecksAsync(
        CancellationToken cancellationToken = default);

    Task<GitGlobalConfiguration> GetGlobalConfigurationAsync(
        CancellationToken cancellationToken = default);

    Task SaveGlobalConfigurationAsync(
        GitGlobalConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<GitRelease> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default);

    Task<GitInstallationStatus> InstallOrUpgradeLatestAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task UninstallAsync(CancellationToken cancellationToken = default);
}

public sealed record GitRelease(
    string Version,
    Uri DownloadUri,
    string Sha256,
    Uri ReleasePageUri,
    string AssetName);

public sealed record GitInstallationStatus(
    bool IsInstalled,
    string? Version,
    string InstallDirectory,
    string LauncherPath,
    string? Problem = null);

public sealed record GitEnvironmentCheck(
    string Name,
    bool IsAvailable,
    string Result);

public sealed record GitGlobalConfiguration(
    string UserName,
    string UserEmail);
