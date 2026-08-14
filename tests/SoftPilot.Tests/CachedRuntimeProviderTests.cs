using SoftPilot.Domain;
using SoftPilot.Application.Abstractions;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Tests;

[TestClass]
public sealed class CachedRuntimeProviderTests
{
    [TestMethod]
    public async Task GetAvailableAsync_UsesFreshPersistentCatalogForOneDay()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var cache = new RuntimeCatalogCache(layout);
        var firstInner = new TestRuntimeProvider(RuntimeKind.Node, "24.19.0");
        var firstProvider = new CachedRuntimeProvider(firstInner, cache);

        var first = await firstProvider.GetAvailableAsync();
        var secondInner = new TestRuntimeProvider(RuntimeKind.Node, "99.0.0");
        var secondProvider = new CachedRuntimeProvider(secondInner, cache);
        var second = await secondProvider.GetAvailableAsync();

        Assert.AreEqual(1, firstInner.AvailableCallCount);
        Assert.AreEqual(0, secondInner.AvailableCallCount);
        Assert.AreEqual("24.19.0", first.Single().Version);
        Assert.AreEqual("24.19.0", second.Single().Version);
    }

    [TestMethod]
    public async Task RefreshAvailableAsync_BypassesFreshCatalogAndUpdatesIt()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var cache = new RuntimeCatalogCache(layout);
        var initial = new CachedRuntimeProvider(
            new TestRuntimeProvider(RuntimeKind.Java, "21.0.12+8"),
            cache);
        await initial.GetAvailableAsync();
        var refreshedInner = new TestRuntimeProvider(RuntimeKind.Java, "25.0.4+7");
        var refreshed = new CachedRuntimeProvider(refreshedInner, cache);

        var releases = await refreshed.RefreshAvailableAsync();
        var cached = await refreshed.GetCachedCatalogAsync();

        Assert.AreEqual(1, refreshedInner.AvailableCallCount);
        Assert.AreEqual("25.0.4+7", releases.Single().Version);
        Assert.AreEqual("25.0.4+7", cached!.Releases.Single().Version);
    }

    [TestMethod]
    public async Task GetAvailableAsync_RefreshesExpiredCatalog()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var cache = new RuntimeCatalogCache(layout);
        await cache.SaveAsync(new RuntimeCatalogCacheEntry(
            RuntimeKind.Python,
            DateTimeOffset.UtcNow.AddDays(-2),
            [Release(RuntimeKind.Python, "3.13.1")]));
        var inner = new TestRuntimeProvider(RuntimeKind.Python, "3.14.7");
        var provider = new CachedRuntimeProvider(inner, cache);

        var releases = await provider.GetAvailableAsync();

        Assert.AreEqual(1, inner.AvailableCallCount);
        Assert.AreEqual("3.14.7", releases.Single().Version);
    }

    [TestMethod]
    public async Task LoadAsync_RejectsLegacyCatalogWithoutReleasePageUri()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var cache = new RuntimeCatalogCache(layout);
        await cache.SaveAsync(new RuntimeCatalogCacheEntry(
            RuntimeKind.Java,
            DateTimeOffset.UtcNow,
            [new RuntimeRelease(
                RuntimeKind.Java,
                "25.0.4+7",
                RuntimeArchitecture.X64,
                new Uri("https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4%2B7/jdk.zip"),
                null)]));

        Assert.IsNull(await cache.LoadAsync(RuntimeKind.Java));
    }

    private static RuntimeRelease Release(RuntimeKind kind, string version) => new(
        kind,
        version,
        RuntimeArchitecture.X64,
        new Uri($"https://example.invalid/{kind}/{version}.zip"),
        null,
        ReleasePageUri: new Uri($"https://example.invalid/{kind}/{version}/"));
}
