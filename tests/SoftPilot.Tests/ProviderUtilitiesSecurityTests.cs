using System.Net;
using SoftPilot.Application;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Tests;

[TestClass]
public sealed class ProviderUtilitiesSecurityTests
{
    [TestMethod]
    public async Task GetRequiredStringAsync_WhenRedirectedAddressIsNotHttps_RejectsMetadata()
    {
        using var client = new HttpClient(new RedirectedAddressHandler());

        await Assert.ThrowsAsync<IntegrityException>(() => ProviderUtilities.GetRequiredStringAsync(
            client,
            new Uri("https://example.test/index.json"),
            CancellationToken.None));
    }

    private sealed class RedirectedAddressHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cdn.example.test/index.json"),
            });
    }
}
