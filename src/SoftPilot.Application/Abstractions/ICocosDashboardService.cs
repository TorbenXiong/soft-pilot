namespace SoftPilot.Application.Abstractions;

public interface ICocosDashboardService
{
    Task<CocosDashboardInstallationStatus> GetInstalledStatusAsync(
        CancellationToken cancellationToken = default);

    Task<CocosDashboardRelease> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default);

    Task<CocosDashboardInstallationStatus> InstallOrUpgradeLatestAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task UninstallAsync(CancellationToken cancellationToken = default);

    Task UninstallAsync(
        CocosDashboardUninstallOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record CocosDashboardUninstallOptions(bool DeleteData = false);

public sealed record CocosDashboardRelease(
    string Version,
    Uri DownloadUri,
    Uri ReleasePageUri,
    string AssetName,
    string Sha256);

public sealed record CocosDashboardInstallationStatus(
    bool IsInstalled,
    string? Version,
    string? InstallDirectory,
    string? LauncherPath,
    string? Problem = null);
