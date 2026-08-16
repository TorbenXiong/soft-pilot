using System.IO.Compression;
using System.Security.Cryptography;
using SoftPilot.Application.Abstractions;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Tests;

[TestClass]
public sealed class PythonInstallManagerProvisionerTests
{
    private const string PackageName = "PythonSoftwareFoundation.PythonManager";
    private const string PackageFamily = "PythonSoftwareFoundation.PythonManager_3847v3x7pw1km";
    private const string Publisher =
        "CN=Python Software Foundation, O=Python Software Foundation, L=Beaverton, S=Oregon, C=US";
    private static readonly Version ManagerVersion = new(26, 3, 240, 0);

    [TestMethod]
    public async Task AcquireAsync_ReusesUserPackageAndNeverRemovesIt()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var system = new FakeManagerSystem
        {
            Package = CreatePackage(),
        };
        var provisioner = new PythonInstallManagerProvisioner(
            new FakeManagerDownloadService(),
            layout,
            system);

        await using (var lease = await provisioner.AcquireAsync(progress: null, CancellationToken.None))
        {
            Assert.AreEqual(@"C:\WindowsApps\PythonManager\pymanager.exe", lease.ExecutablePath);
        }

        Assert.AreEqual(0, system.InstallCount);
        Assert.AreEqual(0, system.RemoveCount);
        Assert.IsNotNull(system.Package);
    }

    [TestMethod]
    public async Task AcquireAsync_TemporaryPackageIsRemovedAfterUse()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var system = new FakeManagerSystem();
        var provisioner = new PythonInstallManagerProvisioner(
            new FakeManagerDownloadService(),
            layout,
            system);

        await using (var lease = await provisioner.AcquireAsync(progress: null, CancellationToken.None))
        {
            Assert.IsNotNull(system.Package);
            Assert.AreEqual(1, system.InstallCount);
        }

        Assert.AreEqual(1, system.RemoveCount);
        Assert.IsNull(system.Package);
    }

    [TestMethod]
    public async Task AcquireAsync_ConcurrentRequestsAreSerializedUntilCleanupFinishes()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var system = new FakeManagerSystem();
        var provisioner = new PythonInstallManagerProvisioner(
            new FakeManagerDownloadService(),
            layout,
            system);

        var first = await provisioner.AcquireAsync(progress: null, CancellationToken.None);
        var secondTask = Task.Run(async () =>
            await provisioner.AcquireAsync(progress: null, CancellationToken.None));
        await Task.Delay(150);
        Assert.IsFalse(secondTask.IsCompleted);

        await first.DisposeAsync();
        await using var second = await secondTask;
        Assert.AreEqual(2, system.InstallCount);
        Assert.AreEqual(1, system.RemoveCount);

        await second.DisposeAsync();
        Assert.AreEqual(2, system.RemoveCount);
    }

    [TestMethod]
    public async Task AcquireAsync_WhenRegistrationFailsAfterCreatingPackage_RemovesPartialPackage()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var system = new FakeManagerSystem
        {
            FailInstallAfterRegistration = true,
        };
        var provisioner = new PythonInstallManagerProvisioner(
            new FakeManagerDownloadService(),
            layout,
            system);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provisioner.AcquireAsync(progress: null, CancellationToken.None));

        Assert.AreEqual(1, system.InstallCount);
        Assert.AreEqual(1, system.RemoveCount);
        Assert.IsNull(system.Package);
    }

    private static PythonInstallManagerPackage CreatePackage() => new(
        PackageName,
        $"{PackageName}_26.3.240.0_x64__3847v3x7pw1km",
        PackageFamily,
        Publisher,
        ManagerVersion);

    private sealed class FakeManagerSystem : IPythonInstallManagerSystem
    {
        private readonly object _sync = new();

        public PythonInstallManagerPackage? Package { get; set; }
        public int InstallCount { get; private set; }
        public int RemoveCount { get; private set; }
        public bool FailInstallAfterRegistration { get; set; }

        public Task<IReadOnlyList<PythonInstallManagerPackage>> FindPackagesAsync(
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                IReadOnlyList<PythonInstallManagerPackage> result = Package is null ? [] : [Package];
                return Task.FromResult(result);
            }
        }

        public string? FindPackageExecutable(PythonInstallManagerPackage package) =>
            @"C:\WindowsApps\PythonManager\pymanager.exe";

        public bool PackageFamilyAliasExists(PythonInstallManagerPackage package) => Package is not null;

        public string? FindExecutableOnPath() => null;

        public Task<AuthenticodeVerification> VerifyAuthenticodeAsync(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AuthenticodeVerification(true, Publisher));

        public Task InstallPackageAsync(string path, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                InstallCount++;
                Package = CreatePackage();
            }

            if (FailInstallAfterRegistration)
            {
                throw new InvalidOperationException("simulated registration failure");
            }

            return Task.CompletedTask;
        }

        public Task RemovePackageAsync(string packageFullName, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                RemoveCount++;
                Package = null;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeManagerDownloadService : IDownloadService
    {
        public async Task<DownloadResult> DownloadAsync(
            Uri source,
            string destinationPath,
            string? expectedSha256 = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (destinationPath.EndsWith(".appinstaller", StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(destinationPath, AppInstallerXml(), cancellationToken);
            }
            else
            {
                CreateMsix(destinationPath);
            }

            var info = new FileInfo(destinationPath);
            return new DownloadResult(destinationPath, new string('0', 64), info.Length);
        }

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken = default)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        private static string AppInstallerXml() => $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"
                          Version="{{ManagerVersion}}"
                          Uri="https://www.python.org/ftp/python/pymanager/pymanager.appinstaller">
              <MainPackage Name="{{PackageName}}"
                           Publisher="{{Publisher}}"
                           Version="{{ManagerVersion}}"
                           ProcessorArchitecture="x64"
                           Uri="https://www.python.org/ftp/python/pymanager/python-manager-26.3.msix" />
            </AppInstaller>
            """;

        private static void CreateMsix(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            var entry = archive.CreateEntry("AppxManifest.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write($$"""
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="{{PackageName}}"
                            Publisher="{{Publisher}}"
                            Version="{{ManagerVersion}}"
                            ProcessorArchitecture="x64" />
                </Package>
                """);
        }
    }
}
