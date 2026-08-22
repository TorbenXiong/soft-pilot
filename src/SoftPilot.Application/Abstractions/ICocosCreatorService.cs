namespace SoftPilot.Application.Abstractions;

public interface ICocosCreatorService
{
    string GetInstallDirectory(string version);

    Task<IReadOnlyList<CocosCreatorRelease>> GetAvailableReleasesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CocosCreatorInstallationStatus>> GetInstalledStatusesAsync(
        CancellationToken cancellationToken = default);

    Task<CocosCreatorInstallationStatus> InstallAsync(
        CocosCreatorRelease release,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CocosCreatorInstallationStatus> UpgradeAsync(
        CocosCreatorRelease release,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task UninstallAsync(
        string version,
        CancellationToken cancellationToken = default);
}

public sealed record CocosCreatorRelease(
    string Version,
    Uri DownloadUri,
    Uri ReleasePageUri,
    string AssetName);

public sealed record CocosCreatorInstallationStatus(
    string Version,
    string InstallDirectory,
    string LauncherPath,
    string? Problem = null)
{
    public bool IsHealthy => string.IsNullOrWhiteSpace(Problem);
}
