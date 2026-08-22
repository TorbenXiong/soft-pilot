using SoftPilot.Application;
using SoftPilot.Application.Abstractions;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Tools;

namespace SoftPilot.Tests;

[TestClass]
public sealed class CocosDashboardServiceTests
{
    [TestMethod]
    public void InstallationLayout_PlacesCocosInAppDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);

        Assert.AreEqual(Path.Combine(layout.AppDirectory, "cocos"), layout.CocosDirectory);
    }

    [TestMethod]
    public void ParseLatestRelease_AcceptsLatestOfficialWindowsInstaller()
    {
        const string html = """
            <script>
            window.__NUXT__={dashboardLatest:"https:\u002F\u002Fdownload.cocos.com\u002FCocosDashboard\u002Fv2.2.1\u002FCocosDashboard-v2.2.1-win-112616.exe"};
            </script>
            """;

        var release = CocosDashboardService.ParseLatestRelease(html);

        Assert.AreEqual("2.2.1", release.Version);
        Assert.AreEqual("CocosDashboard-v2.2.1-win-112616.exe", release.AssetName);
        Assert.AreEqual(
            "https://download.cocos.com/CocosDashboard/v2.2.1/CocosDashboard-v2.2.1-win-112616.exe",
            release.DownloadUri.AbsoluteUri);
        Assert.AreEqual(CocosDashboardService.ReleasePageUri, release.ReleasePageUri);
        Assert.AreEqual(
            "f47e0bfc5bccd452361160784c6062b0b30b9933854ebc04346b8322aee3d9aa",
            release.Sha256);
    }

    [TestMethod]
    public void ParseLatestRelease_SelectsHighestVersionAndRejectsUntrustedPages()
    {
        const string officialHtml = """
            https://download.cocos.com/CocosDashboard/v2.1.4/CocosDashboard-v2.1.4-win-112614.exe
            https://download.cocos.com/CocosDashboard/v2.2.1/CocosDashboard-v2.2.1-win-112616.exe
            """;
        const string untrustedHtml =
            "https://example.test/CocosDashboard/v9.9.9/CocosDashboard-v9.9.9-win-010101.exe";

        Assert.AreEqual("2.2.1", CocosDashboardService.ParseLatestRelease(officialHtml).Version);
        Assert.ThrowsExactly<IntegrityException>(() =>
            CocosDashboardService.ParseLatestRelease(untrustedHtml));
    }

    [TestMethod]
    public void ParseLatestRelease_RejectsMismatchedDirectoryAndAssetVersions()
    {
        const string html =
            "https://download.cocos.com/CocosDashboard/v2.2.1/CocosDashboard-v9.9.9-win-112616.exe";

        Assert.ThrowsExactly<IntegrityException>(() => CocosDashboardService.ParseLatestRelease(html));
    }

    [TestMethod]
    public void ParseLatestRelease_RejectsNewerInstallerWithoutPinnedHash()
    {
        const string html = """
            https://download.cocos.com/CocosDashboard/v2.2.1/CocosDashboard-v2.2.1-win-112616.exe
            https://download.cocos.com/CocosDashboard/v2.3.0/CocosDashboard-v2.3.0-win-010101.exe
            """;

        Assert.ThrowsExactly<IntegrityException>(() => CocosDashboardService.ParseLatestRelease(html));
    }

    [TestMethod]
    [DataRow("2.2.1.2616", "2.2.1")]
    [DataRow("Cocos Dashboard v2.1.4", "2.1.4")]
    public void NormalizeVersion_ReadsSemanticVersion(string value, string expected) =>
        Assert.AreEqual(expected, CocosDashboardService.NormalizeVersion(value));

    [TestMethod]
    public void PublisherChecks_RequireOfficialCompanyIdentity()
    {
        const string subject =
            "CN=\"Xiamen Yaji Software Co., Ltd.\", O=\"Xiamen Yaji Software Co., Ltd.\", L=Xiamen, S=Fujian, C=CN";

        Assert.IsTrue(CocosDashboardService.IsCocosPublisher(subject));
        Assert.IsFalse(CocosDashboardService.IsCocosPublisher("CN=Untrusted Publisher, O=Untrusted Publisher"));
    }

    [TestMethod]
    public async Task InstallOrUpgradeLatestAsync_VerifiesInstallerAndInstalledLauncher()
    {
        using var temporary = new TemporaryDirectory();
        var system = new FakeCocosSystem();
        var downloads = new FakeDownloadService();
        var service = CreateService(temporary.Path, downloads, system);

        var status = await service.InstallOrUpgradeLatestAsync();

        Assert.IsTrue(status.IsInstalled);
        Assert.AreEqual("2.2.1", status.Version);
        Assert.AreEqual(1, downloads.CallCount);
        Assert.AreEqual(
            "f47e0bfc5bccd452361160784c6062b0b30b9933854ebc04346b8322aee3d9aa",
            downloads.ExpectedSha256);
        Assert.AreEqual(3, system.SignatureVerificationCount);
        Assert.AreEqual(1, system.ExtractCount);
        Assert.AreEqual(
            Path.Combine(temporary.Path, "SoftPilotData", "app", "cocos"),
            status.InstallDirectory);
    }

    [TestMethod]
    public async Task GetInstalledStatusAsync_RejectsUntrustedInstalledLauncher()
    {
        using var temporary = new TemporaryDirectory();
        var system = new FakeCocosSystem();
        var service = CreateService(temporary.Path, new FakeDownloadService(), system);
        var installDirectory = Path.Combine(temporary.Path, "SoftPilotData", "app", "cocos");
        await system.ExtractPortableAsync(
            "installer.exe",
            Path.Combine(temporary.Path, "package"),
            installDirectory,
            CancellationToken.None);
        system.Signature = new CocosAuthenticodeVerification(false, "CN=Untrusted, O=Untrusted");

        var status = await service.GetInstalledStatusAsync();

        Assert.IsTrue(status.IsInstalled);
        StringAssert.Contains(status.Problem, "已禁止启动");
    }

    [TestMethod]
    public async Task InstallOrUpgradeLatestAsync_RejectsUntrustedInstallerBeforeExecution()
    {
        using var temporary = new TemporaryDirectory();
        var system = new FakeCocosSystem
        {
            Signature = new CocosAuthenticodeVerification(false, "CN=Untrusted, O=Untrusted"),
        };
        var service = CreateService(temporary.Path, new FakeDownloadService(), system);

        await Assert.ThrowsExactlyAsync<IntegrityException>(() => service.InstallOrUpgradeLatestAsync());

        Assert.AreEqual(0, system.ExtractCount);
    }

    [TestMethod]
    public async Task InstallOrUpgradeLatestAsync_UsesOfficialCdnConnectionFallbackForForbiddenResponse()
    {
        using var temporary = new TemporaryDirectory();
        var primaryDownloads = new FakeDownloadService
        {
            DownloadFailure = new HttpRequestException(
                "Forbidden",
                null,
                System.Net.HttpStatusCode.Forbidden),
        };
        var fallbackDownloads = new FakeDownloadService();
        var system = new FakeCocosSystem();
        var service = CreateService(temporary.Path, primaryDownloads, system, fallbackDownloads);

        var status = await service.InstallOrUpgradeLatestAsync();

        Assert.IsTrue(status.IsInstalled);
        Assert.AreEqual(1, primaryDownloads.CallCount);
        Assert.AreEqual(1, fallbackDownloads.CallCount);
        Assert.AreEqual(
            "f47e0bfc5bccd452361160784c6062b0b30b9933854ebc04346b8322aee3d9aa",
            fallbackDownloads.ExpectedSha256);
    }

    [TestMethod]
    public async Task UninstallAsync_RemovesManagedDirectoryAndCacheButPreservesUserDataByDefault()
    {
        using var temporary = new TemporaryDirectory();
        var system = new FakeCocosSystem();
        var service = CreateService(temporary.Path, new FakeDownloadService(), system);
        var status = await service.InstallOrUpgradeLatestAsync();
        var preservedData = Path.Combine(temporary.Path, "user-profile", ".Cocos");
        Directory.CreateDirectory(preservedData);

        await service.UninstallAsync();

        Assert.IsFalse(Directory.Exists(status.InstallDirectory));
        Assert.IsTrue(Directory.Exists(preservedData));
        Assert.AreEqual(
            0,
            Directory.GetFiles(
                Path.Combine(temporary.Path, "SoftPilotData", "cache", "downloads"),
                "CocosDashboard-*.exe").Length);
    }

    [TestMethod]
    public async Task UninstallAsync_DeleteDataRemovesDashboardUserData()
    {
        using var temporary = new TemporaryDirectory();
        var system = new FakeCocosSystem();
        var service = CreateService(temporary.Path, new FakeDownloadService(), system);
        var status = await service.InstallOrUpgradeLatestAsync();
        var userData = Path.Combine(temporary.Path, "user-profile", ".Cocos");
        Directory.CreateDirectory(userData);
        await File.WriteAllTextAsync(Path.Combine(userData, "settings.json"), "{}");

        await service.UninstallAsync(new CocosDashboardUninstallOptions(DeleteData: true));

        Assert.IsFalse(Directory.Exists(status.InstallDirectory));
        Assert.IsFalse(Directory.Exists(userData));
    }

    [TestMethod]
    public void GetRemovalDirectoryForSource_UsesSourceVolumeForCrossVolumeUserData()
    {
        const string preferred = @"D:\SoftPilot\SoftPilotData\staging\cocos-uninstall-test";

        Assert.AreEqual(
            preferred,
            CocosDashboardService.GetRemovalDirectoryForSource(
                @"D:\SoftPilot\SoftPilotData\app\cocos",
                preferred,
                ".softpilot-cocos-uninstall-test"));
        Assert.AreEqual(
            @"C:\Users\tester\.softpilot-cocos-uninstall-test",
            CocosDashboardService.GetRemovalDirectoryForSource(
                @"C:\Users\tester\.Cocos",
                preferred,
                ".softpilot-cocos-uninstall-test"));
    }

    [TestMethod]
    public async Task Upgrade_RestoresPreviousDirectoryWhenCommittedHealthCheckFails()
    {
        using var temporary = new TemporaryDirectory();
        var system = new FakeCocosSystem();
        var service = CreateService(temporary.Path, new FakeDownloadService(), system);
        var installed = await service.InstallOrUpgradeLatestAsync();
        var marker = Path.Combine(installed.InstallDirectory!, "previous-version.marker");
        await File.WriteAllTextAsync(marker, "preserve");
        system.SignatureFailureOnCall = system.SignatureVerificationCount + 4;

        await Assert.ThrowsExactlyAsync<SoftPilotException>(() => service.InstallOrUpgradeLatestAsync());

        Assert.IsTrue(File.Exists(marker));
        var restored = await service.GetInstalledStatusAsync();
        Assert.IsTrue(restored.IsInstalled);
        Assert.IsNull(restored.Problem);
    }

    private static CocosDashboardService CreateService(
        string root,
        IDownloadService downloads,
        ICocosDashboardSystem system,
        IDownloadService? officialCdnFallbackDownloads = null)
    {
        var layout = new WindowsInstallationLayout(root);
        layout.EnsureWorkspace();
        return officialCdnFallbackDownloads is null
            ? new CocosDashboardService(
                new HttpClient(new CatalogHandler()),
                downloads,
                layout,
                new InMemoryStateStore(),
                system,
                downloads,
                Path.Combine(root, "user-profile"))
            : new CocosDashboardService(
                new HttpClient(new CatalogHandler()),
                downloads,
                layout,
                new InMemoryStateStore(),
                system,
                officialCdnFallbackDownloads,
                Path.Combine(root, "user-profile"));
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "https://download.cocos.com/CocosDashboard/v2.2.1/CocosDashboard-v2.2.1-win-112616.exe"),
            });
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        public int CallCount { get; private set; }
        public string? ExpectedSha256 { get; private set; }
        public Exception? DownloadFailure { get; init; }

        public async Task<DownloadResult> DownloadAsync(
            Uri source,
            string destinationPath,
            string? expectedSha256 = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ExpectedSha256 = expectedSha256;
            if (DownloadFailure is not null)
            {
                throw DownloadFailure;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(destinationPath, "installer", cancellationToken);
            return new DownloadResult(destinationPath, new string('a', 64), 9);
        }

        public Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new string('a', 64));
    }

    private sealed class FakeCocosSystem : ICocosDashboardSystem
    {
        private const string TrustedSubject =
            "CN=Xiamen Yaji Software Co., Ltd., O=Xiamen Yaji Software Co., Ltd., L=Xiamen, C=CN";
        public CocosAuthenticodeVerification Signature { get; set; } = new(true, TrustedSubject);
        public int? SignatureFailureOnCall { get; set; }
        public int SignatureVerificationCount { get; private set; }
        public int ExtractCount { get; private set; }

        public string? GetProductVersion(string launcherPath) => "2.2.1.2616";

        public Task<CocosAuthenticodeVerification> VerifyAuthenticodeAsync(
            string path,
            CancellationToken cancellationToken)
        {
            SignatureVerificationCount++;
            return Task.FromResult(
                SignatureVerificationCount == SignatureFailureOnCall
                    ? new CocosAuthenticodeVerification(false, "CN=Untrusted, O=Untrusted")
                    : Signature);
        }

        public async Task ExtractPortableAsync(
            string installerPath,
            string packageDirectory,
            string payloadDirectory,
            CancellationToken cancellationToken)
        {
            ExtractCount++;
            Directory.CreateDirectory(packageDirectory);
            Directory.CreateDirectory(payloadDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(payloadDirectory, "CocosDashboard.exe"),
                "launcher",
                cancellationToken);
        }
    }
}
