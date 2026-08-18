namespace SoftPilot.Application.Abstractions;

public interface IDownloadService
{
    Task<DownloadResult> DownloadAsync(
        Uri source,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<DownloadResult> DownloadAsync(
        IReadOnlyList<DownloadSourceCandidate> sources,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var source = sources.FirstOrDefault()
            ?? throw new ArgumentException("下载候选不能为空。", nameof(sources));
        return DownloadAsync(
            source.Uri,
            destinationPath,
            expectedSha256,
            progress,
            cancellationToken);
    }

    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);
}

public sealed record DownloadSourceCandidate(
    string DisplayName,
    Uri Uri);

public sealed record DownloadResult(string Path, string Sha256, long Length);
