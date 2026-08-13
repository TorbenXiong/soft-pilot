using System.Buffers;
using System.Net;
using System.Security.Cryptography;

namespace SoftPilot.Infrastructure.IO;

public sealed class HttpDownloadService : IDownloadService
{
    private readonly HttpClient _client;

    public HttpDownloadService(HttpClient client)
    {
        _client = client;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("SoftPilot/1.0 (+https://github.com/TorbenXiong/soft-pilot)");
        _client.Timeout = Timeout.InfiniteTimeSpan;
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
}
