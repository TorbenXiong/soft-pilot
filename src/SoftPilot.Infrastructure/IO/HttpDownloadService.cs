using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SoftPilot.Infrastructure.IO;

public sealed class HttpDownloadService : IDownloadService
{
    private const int ProbeBytes = 64 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    private readonly HttpClient _client;

    public HttpDownloadService(HttpClient client)
    {
        _client = client;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("SoftPilot/1.0 (+https://github.com/TorbenXiong/soft-pilot)");
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<DownloadResult> DownloadAsync(
        IReadOnlyList<DownloadSourceCandidate> sources,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var distinctSources = sources
            .DistinctBy(source => source.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var primarySource = distinctSources.FirstOrDefault()
            ?? throw new ArgumentException("下载候选不能为空。", nameof(sources));

        if (distinctSources.Length == 1)
        {
            progress?.Report(new OperationProgress("source", null, $"下载源：{primarySource.DisplayName}"));
            return await DownloadAsync(
                primarySource.Uri,
                destinationPath,
                expectedSha256,
                progress,
                cancellationToken);
        }

        progress?.Report(new OperationProgress("source", null, "正在探测可用下载源"));
        var probes = await Task.WhenAll(distinctSources.Select(source => ProbeAsync(source, cancellationToken)));
        var orderedSources = probes
            .OrderBy(probe => probe.Succeeded ? 0 : 1)
            .ThenBy(probe => probe.Elapsed)
            .Select(probe => probe.Source)
            .ToArray();

        Exception? firstFailure = null;
        foreach (var source in orderedSources)
        {
            progress?.Report(new OperationProgress("source", null, $"已选择下载源：{source.DisplayName}"));
            try
            {
                return await DownloadAsync(
                    source.Uri,
                    destinationPath,
                    expectedSha256,
                    progress,
                    cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                firstFailure ??= exception;
                progress?.Report(new OperationProgress(
                    "source",
                    null,
                    $"{source.DisplayName} 网络请求失败，正在尝试其他来源"));
            }
        }

        throw new SoftPilotException(
            "所有下载源均不可用，请检查网络连接或稍后重试。",
            firstFailure!);
    }

    public async Task<DownloadResult> DownloadAsync(
        Uri source,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrityException($"拒绝非 HTTPS 下载地址：{source}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var partialPath = destinationPath + $".{Guid.NewGuid():N}.partial";

        try
        {
            using var response = await _client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalAddress = response.RequestMessage?.RequestUri ?? source;
            if (!string.Equals(finalAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new IntegrityException(
                    $"下载地址重定向到了非 HTTPS 地址：{finalAddress.GetLeftPart(UriPartial.Path)}");
            }

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                throw new HttpRequestException("下载服务不接受未解析的重定向响应。");
            }

            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            long received = 0;
            string actualSha256;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        progress?.Report(new OperationProgress(
                            "download",
                            total > 0 ? (double)received / total.Value * 100 : null,
                            source.GetLeftPart(UriPartial.Path)));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await output.FlushAsync(cancellationToken);
                actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            VerifyHash(expectedSha256, actualSha256, source);

            File.Move(partialPath, destinationPath, overwrite: true);
            return new DownloadResult(destinationPath, actualSha256, received);
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }
    }

    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<ProbeResult> ProbeAsync(
        DownloadSourceCandidate source,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(source.Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new ProbeResult(source, false, TimeSpan.MaxValue);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Uri);
            request.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var finalAddress = response.RequestMessage?.RequestUri ?? source.Uri;
            if (!string.Equals(finalAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !response.IsSuccessStatusCode)
            {
                return new ProbeResult(source, false, TimeSpan.MaxValue);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            var received = 0;
            try
            {
                while (received < ProbeBytes)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, ProbeBytes - received)),
                        timeout.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new ProbeResult(source, received > 0, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(source, false, TimeSpan.MaxValue);
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(source, false, TimeSpan.MaxValue);
        }
    }

    private static void VerifyHash(string? expected, string actual, Uri source)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        byte[] expectedBytes;
        byte[] actualBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expected.Trim());
            actualBytes = Convert.FromHexString(actual);
        }
        catch (FormatException exception)
        {
            throw new IntegrityException($"无效的 SHA-256 值：{exception.Message}");
        }

        if (expectedBytes.Length != 32 || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new IntegrityException($"{source} 的 SHA-256 校验失败。");
        }
    }

    private sealed record ProbeResult(
        DownloadSourceCandidate Source,
        bool Succeeded,
        TimeSpan Elapsed);
}
