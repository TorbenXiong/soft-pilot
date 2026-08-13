using System.Net;
using System.Security.Cryptography;
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

        var result = await service.DownloadAsync(
            new Uri("https://example.test/runtime.zip"),
            destination,
            expectedHash);

        Assert.IsTrue(File.Exists(destination));
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(destination));
        Assert.AreEqual(content.Length, result.Length);
        Assert.IsEmpty(Directory.EnumerateFiles(sandbox.Path, "*.partial"));
    }

    private sealed class StaticContentHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }
}
