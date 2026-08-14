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

    private sealed class ProgressRecorder : IProgress<OperationProgress>
    {
        public List<OperationProgress> Values { get; } = [];

        public void Report(OperationProgress value) => Values.Add(value);
    }
}
