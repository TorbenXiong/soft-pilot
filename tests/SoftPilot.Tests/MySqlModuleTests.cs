using System.Security.Cryptography;
using System.IO.Compression;
using SoftPilot.Application.Abstractions;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Runtime;
using SoftPilot.Infrastructure.Security;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Tests;

[TestClass]
public sealed class MySqlModuleTests
{
    [TestMethod]
    public async Task PrerequisiteInstaller_WhenCompatibleRuntimeExists_DoesNotDownloadOrInstall()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = new FakeDownloadService();
        var system = new FakePrerequisiteSystem { InstalledVersion = new Version(14, 44, 35211) };
        var installer = new MySqlPrerequisiteInstaller(
            downloads,
            new WindowsInstallationLayout(sandbox.Path),
            system);

        await installer.EnsureInstalledAsync();

        Assert.AreEqual(0, downloads.DownloadCount);
        Assert.AreEqual(0, system.InstallCount);
    }

    [TestMethod]
    public async Task PrerequisiteInstaller_WhenMissing_VerifiesMicrosoftSignatureBeforeInstall()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = new FakeDownloadService();
        var system = new FakePrerequisiteSystem();
        var installer = new MySqlPrerequisiteInstaller(
            downloads,
            new WindowsInstallationLayout(sandbox.Path),
            system);

        await installer.EnsureInstalledAsync();

        Assert.AreEqual(MySqlPrerequisiteInstaller.DownloadUri, downloads.Source);
        Assert.AreEqual(1, system.VerifyCount);
        Assert.AreEqual(1, system.InstallCount);
        Assert.IsTrue(system.VerifyHappenedBeforeInstall);
    }

    [TestMethod]
    public async Task PrerequisiteInstaller_WhenPublisherIsNotMicrosoft_RefusesInstall()
    {
        using var sandbox = new TemporaryDirectory();
        var system = new FakePrerequisiteSystem
        {
            Signature = new MySqlAuthenticodeVerification(true, "CN=Untrusted Publisher, O=Untrusted Publisher"),
        };
        var installer = new MySqlPrerequisiteInstaller(
            new FakeDownloadService(),
            new WindowsInstallationLayout(sandbox.Path),
            system);

        await Assert.ThrowsAsync<SoftPilot.Application.IntegrityException>(() => installer.EnsureInstalledAsync());

        Assert.AreEqual(0, system.InstallCount);
    }

    [TestMethod]
    public async Task PrerequisiteInstaller_WhenRestartIsRequired_StopsMySqlInstallation()
    {
        using var sandbox = new TemporaryDirectory();
        var system = new FakePrerequisiteSystem { RestartRequired = true };
        var installer = new MySqlPrerequisiteInstaller(
            new FakeDownloadService(),
            new WindowsInstallationLayout(sandbox.Path),
            system);

        var exception = await Assert.ThrowsAsync<SoftPilot.Application.SoftPilotException>(
            () => installer.EnsureInstalledAsync());

        StringAssert.Contains(exception.Message, "重启");
        Assert.AreEqual(1, system.InstallCount);
    }

    [TestMethod]
    public void SupportedCatalog_UsesOfficialSignedWindowsArchives()
    {
        var releases = MySqlRuntimeProvider.GetSupportedReleases();

        CollectionAssert.AreEqual(new[] { "8.4.11", "5.7.44" }, releases.Select(item => item.Version).ToArray());
        Assert.IsTrue(releases[0].IsLongTermSupport);
        Assert.IsFalse(releases[1].IsLongTermSupport);
        Assert.IsTrue(releases.All(item => item.DownloadUri.Host == "cdn.mysql.com"));
        Assert.IsTrue(releases.All(item => item.SignatureUri?.AbsoluteUri == item.DownloadUri.AbsoluteUri + ".asc"));
    }

    [TestMethod]
    public void CurlFallback_AllowsOnlyHttpsAndKeepsCertificateValidationEnabled()
    {
        var arguments = MySqlRuntimeProvider.BuildCurlArguments(
            new Uri("https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip"),
            @"D:\cache\mysql.partial");

        CollectionAssert.Contains(arguments, "--proto");
        CollectionAssert.Contains(arguments, "--proto-redir");
        Assert.AreEqual(2, arguments.Count(argument => argument == "=https"));
        Assert.IsFalse(arguments.Contains("--insecure"));
        Assert.AreEqual("https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip", arguments[^1]);
    }

    [TestMethod]
    public async Task PrepareAsync_WhenSignedArchiveIsCached_DoesNotDownloadAgain()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var archivePath = Path.Combine(layout.DownloadsDirectory, "mysql-8.4.11-winx64.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("mysql-8.4.11-winx64/bin/mysqld.exe");
        }
        await File.WriteAllTextAsync(archivePath + ".asc", "test signature");
        var downloads = new FakeDownloadService();
        using var client = new HttpClient(new StaticKeyHandler());
        var signatures = new AcceptingSignatureVerificationService();
        var provider = new MySqlRuntimeProvider(
            client,
            downloads,
            signatures,
            layout,
            new SoftPilot.Infrastructure.IO.ProcessRunner(),
            new MySqlPrerequisiteInstaller(
                downloads,
                layout,
                new FakePrerequisiteSystem { InstalledVersion = new Version(14, 44, 35211) }));
        var staging = Path.Combine(layout.StagingDirectory, "mysql-cache-test");

        await provider.PrepareAsync(MySqlRuntimeProvider.GetSupportedReleases()[0], staging);

        Assert.AreEqual(0, downloads.DownloadCount);
        Assert.AreEqual(1, signatures.VerifyCount);
        Assert.IsTrue(File.Exists(Path.Combine(staging, "bin", "mysqld.exe")));
    }

    [TestMethod]
    public void BuildDefaultConfig_BindsOnlyLoopbackAndUsesIsolatedPaths()
    {
        var config = MySqlServiceManager.BuildDefaultConfig(
            @"D:\SoftPilot\SoftPilotData\app\mysql\8.4.11",
            @"D:\SoftPilot\SoftPilotData\data\mysql\8.4\data",
            @"D:\SoftPilot\SoftPilotData\logs\mysql\8.4\mysql.log");

        StringAssert.Contains(config, "bind-address=127.0.0.1");
        StringAssert.Contains(config, "port=3306");
        StringAssert.Contains(config, "datadir=\"D:/SoftPilot/SoftPilotData/data/mysql/8.4/data\"");
        StringAssert.Contains(config, "skip-name-resolve");
    }

    [TestMethod]
    public void DefaultPorts_AreDifferentForSupportedReleaseLines()
    {
        Assert.AreEqual(3306, MySqlServiceManager.GetDefaultPort("8.4.11"));
        Assert.AreEqual(3307, MySqlServiceManager.GetDefaultPort("5.7.44"));
    }

    [TestMethod]
    public void BuildDefaultConfig_UsesConfiguredInstancePort()
    {
        var config = MySqlServiceManager.BuildDefaultConfig(
            @"D:\SoftPilot\SoftPilotData\app\mysql\5.7.44",
            @"D:\SoftPilot\SoftPilotData\data\mysql\5.7\data",
            @"D:\SoftPilot\SoftPilotData\logs\mysql\5.7\mysql.log",
            13307);

        StringAssert.Contains(config, "port=13307");
        Assert.IsFalse(config.Contains("port=3306", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildBootstrapServerArguments_DisablesTcpAndUsesSharedMemory()
    {
        var arguments = MySqlServiceManager.BuildBootstrapServerArguments("SoftPilotMySql-8-4");

        CollectionAssert.Contains(arguments, "--skip-networking");
        CollectionAssert.Contains(arguments, "--shared-memory");
        CollectionAssert.Contains(arguments, "--shared-memory-base-name=SoftPilotMySql-8-4");
        Assert.IsFalse(arguments.Any(argument => argument.Contains("named-pipe", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void BuildLoopbackRootSql_CreatesOnlyExplicitLoopbackAccount()
    {
        var sql = MySqlServiceManager.BuildLoopbackRootSql("test-password");

        StringAssert.Contains(sql, "'root'@'127.0.0.1'");
        StringAssert.Contains(sql, "CREATE USER IF NOT EXISTS");
        StringAssert.Contains(sql, "GRANT ALL PRIVILEGES");
        Assert.IsFalse(sql.Contains("'root'@'%'", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsDataProtector_RoundTripsForCurrentUser()
    {
        var clear = System.Text.Encoding.UTF8.GetBytes("mysql-secret");

        var protectedValue = WindowsDataProtector.Protect(clear);
        var restored = WindowsDataProtector.Unprotect(protectedValue);

        Assert.IsFalse(clear.SequenceEqual(protectedValue));
        CollectionAssert.AreEqual(clear, restored);
    }

    private sealed class FakePrerequisiteSystem : IMySqlPrerequisiteSystem
    {
        public Version? InstalledVersion { get; set; }
        public MySqlAuthenticodeVerification Signature { get; set; } = new(
            true,
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US");
        public int VerifyCount { get; private set; }
        public int InstallCount { get; private set; }
        public bool VerifyHappenedBeforeInstall { get; private set; }
        public bool RestartRequired { get; set; }

        public Version? GetInstalledVersion() => InstalledVersion;

        public Task<MySqlAuthenticodeVerification> VerifyAuthenticodeAsync(
            string path,
            CancellationToken cancellationToken)
        {
            Assert.IsTrue(File.Exists(path));
            VerifyCount++;
            return Task.FromResult(Signature);
        }

        public Task<MySqlPrerequisiteInstallResult> InstallAsync(
            string path,
            CancellationToken cancellationToken)
        {
            InstallCount++;
            VerifyHappenedBeforeInstall = VerifyCount > 0;
            InstalledVersion = new Version(14, 44, 35211);
            return Task.FromResult(new MySqlPrerequisiteInstallResult(RestartRequired ? 3010 : 0, RestartRequired));
        }
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        public int DownloadCount { get; private set; }
        public Uri? Source { get; private set; }

        public async Task<DownloadResult> DownloadAsync(
            Uri source,
            string destinationPath,
            string? expectedSha256 = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            Source = source;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(destinationPath, "signed installer", cancellationToken);
            var bytes = await File.ReadAllBytesAsync(destinationPath, cancellationToken);
            return new DownloadResult(
                destinationPath,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
        }

        public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }

    private sealed class StaticKeyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("test public key"),
                RequestMessage = request,
            });
    }

    private sealed class AcceptingSignatureVerificationService : ISignatureVerificationService
    {
        public int VerifyCount { get; private set; }

        public Task VerifyDetachedSignatureAsync(
            string contentPath,
            string signaturePath,
            string armoredPublicKey,
            IReadOnlySet<string> allowedFingerprints,
            CancellationToken cancellationToken = default)
        {
            VerifyCount++;
            return Task.CompletedTask;
        }
    }
}
