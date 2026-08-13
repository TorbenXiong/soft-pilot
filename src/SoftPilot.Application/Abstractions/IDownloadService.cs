namespace SoftPilot.Application.Abstractions;

public interface IDownloadService
{
    Task<DownloadResult> DownloadAsync(
        Uri source,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);
}

public sealed record DownloadResult(string Path, string Sha256, long Length);
