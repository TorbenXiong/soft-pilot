using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using SoftPilot.Application;
using SoftPilot.Application.Abstractions;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Tests;

[TestClass]
public sealed class HttpDownloadServiceTests
{
    [TestMethod]
    public async Task DownloadAsync_DisposesPartialFileBeforeAtomicMove()
    {
        using var sandbox = new TemporaryDirectory();
        var content = "SoftPilot download regression"u8.ToArray();
        using var client = new HttpClient(new StaticContentHandler(content));
        var service = new HttpDownloadService(client);
        var destination = Path.Combine(sandbox.Path, "runtime.zip");
        var expectedHash = Convert.ToHexString(SHA256.HashData(content));
        var progress = new ProgressRecorder();

        var result = await service.DownloadAsync(
            new Uri("https://example.test/runtime.zip"),
            destination,
            expectedHash,
            progress);

        Assert.IsTrue(File.Exists(destination));
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(destination));
        Assert.AreEqual(content.Length, result.Length);
        Assert.AreEqual(100, progress.Values.Last().Percentage);
        Assert.IsEmpty(Directory.EnumerateFiles(sandbox.Path, "*.partial"));
    }

    [TestMethod]
    public async Task DownloadAsync_WhenHashDoesNotMatch_RejectsFileAndRemovesPartialContent()
    {
        using var sandbox = new TemporaryDirectory();
        var content = "tampered runtime"u8.ToArray();
        using var client = new HttpClient(new StaticContentHandler(content));
        var service = new HttpDownloadService(client);
        var destination = Path.Combine(sandbox.Path, "runtime.zip");

        await Assert.ThrowsAsync<IntegrityException>(() => service.DownloadAsync(
            new Uri("https://example.test/runtime.zip"),
            destination,
            new string('0', 64)));

        Assert.IsFalse(File.Exists(destination));
        Assert.IsEmpty(Directory.EnumerateFiles(sandbox.Path, "*.partial"));
    }

    [TestMethod]
    public async Task DownloadAsync_WhenSourceIsNotHttps_RejectsBeforeSendingRequest()
    {
        using var sandbox = new TemporaryDirectory();
        var handler = new StaticContentHandler("runtime"u8.ToArray());
        using var client = new HttpClient(handler);
        var service = new HttpDownloadService(client);

        await Assert.ThrowsAsync<IntegrityException>(() => service.DownloadAsync(
            new Uri("http://example.test/runtime.zip"),
            Path.Combine(sandbox.Path, "runtime.zip")));

        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task DownloadAsync_WhenRedirectedAddressIsNotHttps_RejectsResponse()
    {
        using var sandbox = new TemporaryDirectory();
        using var client = new HttpClient(new RedirectedAddressHandler(new Uri("http://cdn.example.test/runtime.zip")));
        var service = new HttpDownloadService(client);
        var destination = Path.Combine(sandbox.Path, "runtime.zip");

        await Assert.ThrowsAsync<IntegrityException>(() => service.DownloadAsync(
            new Uri("https://example.test/runtime.zip"),
            destination));

        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public async Task DownloadAsync_WhenTlsRequestFailsTemporarily_RetriesWithoutWeakeningValidation()
    {
        using var sandbox = new TemporaryDirectory();
        var content = "Git release asset"u8.ToArray();
        var handler = new TransientNetworkFailureHandler(content, failureCount: 2);
        using var client = new HttpClient(handler);
        var service = new HttpDownloadService(client);
        var destination = Path.Combine(sandbox.Path, "git.exe");

        await service.DownloadAsync(
            new Uri("https://github.com/git-for-windows/git/releases/download/version/git.exe"),
            destination,
            Convert.ToHexString(SHA256.HashData(content)));

        Assert.AreEqual(3, handler.RequestCount);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(destination));
        Assert.IsEmpty(Directory.EnumerateFiles(sandbox.Path, "*.partial"));
    }

    [TestMethod]
    public async Task DownloadAsync_WithMultipleSources_UsesFastestSourceByDefault()
    {
        using var sandbox = new TemporaryDirectory();
        var content = CreateProbeContent();
        var handler = new SourceSelectionHandler(content);
        using var client = new HttpClient(handler);
        var service = new HttpDownloadService(client);
        var destination = Path.Combine(sandbox.Path, "runtime.zip");

        await service.DownloadAsync(
            CreateCandidates(),
            destination,
            Convert.ToHexString(SHA256.HashData(content)));

        CollectionAssert.AreEqual(new[] { "mirror.test" }, handler.FullDownloadHosts.ToArray());
        Assert.AreEqual(2, handler.ProbeHosts.Count);
    }

    [TestMethod]
    public async Task DownloadAsync_WhenFastestSourceHasNetworkFailure_FallsBackToOfficialSource()
    {
        using var sandbox = new TemporaryDirectory();
        var content = CreateProbeContent();
        var handler = new SourceSelectionHandler(content) { FailMirrorDownload = true };
        using var client = new HttpClient(handler);
        var service = new HttpDownloadService(client);

        await service.DownloadAsync(
            CreateCandidates(),
            Path.Combine(sandbox.Path, "runtime.zip"),
            Convert.ToHexString(SHA256.HashData(content)));

        CollectionAssert.AreEqual(
            new[] { "mirror.test", "official.test" },
            handler.FullDownloadHosts.ToArray());
    }

    [TestMethod]
    public async Task DownloadAsync_WhenMirrorFailsIntegrityCheck_DoesNotFallback()
    {
        using var sandbox = new TemporaryDirectory();
        var content = CreateProbeContent();
        var handler = new SourceSelectionHandler(content) { TamperMirrorDownload = true };
        using var client = new HttpClient(handler);
        var service = new HttpDownloadService(client);

        await Assert.ThrowsAsync<IntegrityException>(() => service.DownloadAsync(
            CreateCandidates(),
            Path.Combine(sandbox.Path, "runtime.zip"),
            Convert.ToHexString(SHA256.HashData(content))));

        CollectionAssert.AreEqual(new[] { "mirror.test" }, handler.FullDownloadHosts.ToArray());
    }

    private static byte[] CreateProbeContent() =>
        Enumerable.Range(0, 70 * 1024).Select(index => (byte)(index % 251)).ToArray();

    private static IReadOnlyList<DownloadSourceCandidate> CreateCandidates() =>
    [
        new("官方源", new Uri("https://official.test/runtime.zip")),
        new("清华 TUNA 镜像", new Uri("https://mirror.test/runtime.zip")),
    ];

    private sealed class StaticContentHandler(byte[] content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }

    private sealed class RedirectedAddressHandler(Uri finalAddress) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("runtime"u8.ToArray()),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalAddress),
            });
    }

    private sealed class TransientNetworkFailureHandler(byte[] content, int failureCount) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount <= failureCount)
            {
                throw new HttpRequestException(
                    "The SSL connection could not be established.",
                    new System.Security.Authentication.AuthenticationException("Simulated TLS handshake failure."));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request,
            });
        }
    }

    private sealed class SourceSelectionHandler(byte[] content) : HttpMessageHandler
    {
        public bool FailMirrorDownload { get; init; }

        public bool TamperMirrorDownload { get; init; }

        public ConcurrentQueue<string> ProbeHosts { get; } = [];

        public ConcurrentQueue<string> FullDownloadHosts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            if (request.Headers.Range is not null)
            {
                ProbeHosts.Enqueue(host);
                if (host == "official.test")
                {
                    await Task.Delay(75, cancellationToken);
                }

                return CreateResponse(
                    request,
                    content[..Math.Min(content.Length, 64 * 1024)],
                    HttpStatusCode.PartialContent);
            }

            FullDownloadHosts.Enqueue(host);
            if (host == "mirror.test" && FailMirrorDownload)
            {
                throw new HttpRequestException("simulated mirror outage");
            }

            var responseContent = host == "mirror.test" && TamperMirrorDownload
                ? "tampered"u8.ToArray()
                : content;
            return CreateResponse(request, responseContent, HttpStatusCode.OK);
        }

        private static HttpResponseMessage CreateResponse(
            HttpRequestMessage request,
            byte[] responseContent,
            HttpStatusCode statusCode) =>
            new(statusCode)
            {
                Content = new ByteArrayContent(responseContent),
                RequestMessage = request,
            };
    }

    private sealed class ProgressRecorder : IProgress<OperationProgress>
    {
        public List<OperationProgress> Values { get; } = [];

        public void Report(OperationProgress value) => Values.Add(value);
    }
}
