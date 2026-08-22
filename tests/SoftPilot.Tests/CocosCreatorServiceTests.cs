using System.IO.Compression;
using System.Security.Cryptography;
using SoftPilot.Application;
using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Tools;

namespace SoftPilot.Tests;

[TestClass]
public sealed class CocosCreatorServiceTests
{
    private static readonly CocosCreatorRelease Release = new(
        "3.8.8",
        new Uri(
            "https://download.cocos.com/CocosCreator/v3.8.8/CocosCreator-v3.8.8-win-121518.zip"),
        CocosCreatorService.ReleasePageUri,
        "CocosCreator-v3.8.8-win-121518.zip");
    private static readonly CocosCreatorRelease UpgradeRelease = new(
        "3.8.9",
        new Uri(
            "https://download.cocos.com/CocosCreator/v3.8.9/CocosCreator-v3.8.9-win-010101.zip"),
        CocosCreatorService.ReleasePageUri,
        "CocosCreator-v3.8.9-win-010101.zip");

    [TestMethod]
    public void InstallationLayout_PlacesCreatorVersionsAndCachesInManagedDirectories()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);

        Assert.AreEqual(
            Path.Combine(layout.AppDirectory, "cocos-creator"),
            layout.CocosCreatorDirectory);
        Assert.AreEqual(
            Path.Combine(layout.AppDirectory, "cocos-creator", "3.8.8"),
            layout.GetCocosCreatorDirectory("3.8.8"));
        Assert.AreEqual(
            Path.Combine(layout.DownloadsDirectory, "cocos-creator", "3.8.8"),
            layout.GetCocosCreatorDownloadDirectory("3.8.8"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            layout.GetCocosCreatorDirectory("..\\outside"));
    }

    [TestMethod]
    public void ParseReleases_ReturnsStableOfficialWindowsArchivesInDescendingOrder()
    {
        const string html = """
            https:\u002F\u002Fdownload.cocos.com\u002FCocosCreator\u002Fv3.8.7\u002FCocosCreator-v3.8.7-win-080718.zip
            https://download.cocos.com/CocosCreator/v3.8.8/CocosCreator-v3.8.8-win-121518.zip
            https://download.cocos.com/CocosCreator/v3.8.8/CocosCreator-v3.8.8-mac-121518.zip
            """;

        var releases = CocosCreatorService.ParseReleases(html);

        Assert.HasCount(2, releases);
        Assert.AreEqual("3.8.8", releases[0].Version);
        Assert.AreEqual("CocosCreator-v3.8.8-win-121518.zip", releases[0].AssetName);
        Assert.AreEqual(CocosCreatorService.ReleasePageUri, releases[0].ReleasePageUri);
        Assert.AreEqual("3.8.7", releases[1].Version);
    }

    [TestMethod]
    public void ParseReleases_RejectsUntrustedOrMismatchedArchives()
    {
        Assert.ThrowsExactly<IntegrityException>(() => CocosCreatorService.ParseReleases(
            "https://example.test/CocosCreator/v3.8.8/CocosCreator-v3.8.8-win-121518.zip"));
        Assert.ThrowsExactly<IntegrityException>(() => CocosCreatorService.ParseReleases(
            "https://download.cocos.com/CocosCreator/v3.8.8/CocosCreator-v3.8.7-win-121518.zip"));
    }

    [TestMethod]
    public async Task GetAvailableReleasesAsync_ReturnsOnlyLatestStableRelease()
    {
        const string html = """
            https://download.cocos.com/CocosCreator/v3.8.7/CocosCreator-v3.8.7-win-080718.zip
            https://download.cocos.com/CocosCreator/v3.8.8/CocosCreator-v3.8.8-win-121518.zip
            """;
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        layout.EnsureWorkspace();
        var service = new CocosCreatorService(
            new HttpClient(new CatalogHandler(html)),
            new FakeDownloadService(),
            layout,
            new InMemoryStateStore(),
            new FakeCocosCreatorSystem());

        var releases = await service.GetAvailableReleasesAsync();

        Assert.HasCount(1, releases);
        Assert.AreEqual("3.8.8", releases[0].Version);
    }

    [TestMethod]
    public void DownloadRedirectPolicy_RejectsNonOfficialFinalHost()
    {
        var source = Release.DownloadUri;

        Assert.IsTrue(CocosCreatorService.IsTrustedFinalDownloadAddress(
            source,
            new Uri("https://download.cocos.com/CocosCreator/v3.8.8/archive.zip")));
        Assert.IsFalse(CocosCreatorService.IsTrustedFinalDownloadAddress(
            source,
            new Uri("https://example.test/CocosCreator/v3.8.8/archive.zip")));
    }

    [TestMethod]
    public async Task InstallAsync_ExtractsValidatesAndCommitsVersionDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var downloads = new FakeDownloadService();
        var system = new FakeCocosCreatorSystem();
        var service = CreateService(temporary.Path, downloads, system);

        var status = await service.InstallAsync(Release);

        Assert.IsTrue(status.IsHealthy);
        Assert.AreEqual("3.8.8", status.Version);
        Assert.IsTrue(File.Exists(status.LauncherPath));
        Assert.AreEqual(1, downloads.DownloadCount);
        Assert.AreEqual(1, downloads.ComputeCount);
        Assert.AreEqual(2, system.SignatureVerificationCount);
        var cacheDirectory = Path.Combine(
            temporary.Path,
            "SoftPilotData",
            "cache",
            "downloads",
            "cocos-creator",
            "3.8.8");
        Assert.IsTrue(File.Exists(Path.Combine(cacheDirectory, Release.AssetName)));
        Assert.IsTrue(File.Exists(Path.Combine(cacheDirectory, Release.AssetName + ".sha256")));
    }

    [TestMethod]
    public async Task UpgradeAsync_InstallsNewVersionAndPreservesPreviousVersion()
    {
        using var temporary = new TemporaryDirectory();
        var state = new InMemoryStateStore();
        var service = CreateService(
            temporary.Path,
            new FakeDownloadService(),
            new FakeCocosCreatorSystem(),
            state);
        var previous = await service.InstallAsync(Release);

        var upgraded = await service.UpgradeAsync(UpgradeRelease);

        Assert.IsTrue(upgraded.IsHealthy);
        Assert.AreEqual("3.8.9", upgraded.Version);
        Assert.IsTrue(Directory.Exists(previous.InstallDirectory));
        Assert.IsTrue(Directory.Exists(upgraded.InstallDirectory));
        var operations = await state.GetOperationsAsync();
        Assert.HasCount(2, operations);
        Assert.AreEqual("cocos-creator-upgrade", operations[0].Name);
        Assert.AreEqual(OperationStatus.Succeeded, operations[0].Status);
    }

    [TestMethod]
    public async Task InstallAsync_RejectsUntrustedLauncherWithoutCommittingVersion()
    {
        using var temporary = new TemporaryDirectory();
        var system = new FakeCocosCreatorSystem
        {
            Signature = new CocosAuthenticodeVerification(
                false,
                "CN=Untrusted, O=Untrusted"),
        };
        var service = CreateService(temporary.Path, new FakeDownloadService(), system);

        await Assert.ThrowsExactlyAsync<SoftPilotException>(() => service.InstallAsync(Release));

        Assert.IsFalse(Directory.Exists(Path.Combine(
            temporary.Path,
            "SoftPilotData",
            "app",
            "cocos-creator",
            "3.8.8")));
    }

    [TestMethod]
    public async Task UninstallAsync_RemovesOnlySelectedManagedVersionAndCache()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService(
            temporary.Path,
            new FakeDownloadService(),
            new FakeCocosCreatorSystem());
        var installed = await service.InstallAsync(Release);
        var sharedSettings = Path.Combine(temporary.Path, "user-profile", ".CocosCreator");
        var project = Path.Combine(temporary.Path, "projects", "game");
        Directory.CreateDirectory(sharedSettings);
        Directory.CreateDirectory(project);

        await service.UninstallAsync("3.8.8");

        Assert.IsFalse(Directory.Exists(installed.InstallDirectory));
        Assert.IsFalse(Directory.Exists(Path.Combine(
            temporary.Path,
            "SoftPilotData",
            "cache",
            "downloads",
            "cocos-creator",
            "3.8.8")));
        Assert.IsTrue(Directory.Exists(sharedSettings));
        Assert.IsTrue(Directory.Exists(project));
    }

    private static CocosCreatorService CreateService(
        string root,
        IDownloadService downloads,
        ICocosCreatorSystem system,
        IStateStore? state = null)
    {
        var layout = new WindowsInstallationLayout(root);
        layout.EnsureWorkspace();
        return new CocosCreatorService(
            new HttpClient(new CatalogHandler()),
            downloads,
            layout,
            state ?? new InMemoryStateStore(),
            system);
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        private readonly string _content;

        public CatalogHandler(string? content = null)
        {
            _content = content ?? Release.DownloadUri.AbsoluteUri;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_content),
            });
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        public int DownloadCount { get; private set; }
        public int ComputeCount { get; private set; }

        public async Task<DownloadResult> DownloadAsync(
            Uri source,
            string destinationPath,
            string? expectedSha256 = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using (var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entry = archive.CreateEntry("CocosCreator/CocosCreator.exe");
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync("launcher"u8.ToArray(), cancellationToken);
            }

            var hash = await ComputeFileHashAsync(destinationPath, cancellationToken);
            return new DownloadResult(destinationPath, hash, new FileInfo(destinationPath).Length);
        }

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken = default)
        {
            ComputeCount++;
            return await ComputeFileHashAsync(path, cancellationToken);
        }

        private static async Task<string> ComputeFileHashAsync(
            string path,
            CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    private sealed class FakeCocosCreatorSystem : ICocosCreatorSystem
    {
        private const string TrustedSubject =
            "CN=Xiamen Yaji Software Co., Ltd., O=Xiamen Yaji Software Co., Ltd., L=Xiamen, C=CN";

        public CocosAuthenticodeVerification Signature { get; init; } = new(true, TrustedSubject);
        public int SignatureVerificationCount { get; private set; }

        public string? GetProductVersion(string launcherPath) =>
            launcherPath.Contains("3.8.9", StringComparison.Ordinal) ? "3.8.9.0" : "3.8.8.0";

        public Task<CocosAuthenticodeVerification> VerifyAuthenticodeAsync(
            string path,
            CancellationToken cancellationToken)
        {
            SignatureVerificationCount++;
            return Task.FromResult(Signature);
        }
    }
}
