using System.Diagnostics;
using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Tests;

[TestClass]
public sealed class OperationCoordinatorTests
{
    private static readonly IRedisServiceManager StoppedRedis =
        new StubRedisServiceManager(new RedisServiceStatus(false));

    [TestMethod]
    public async Task UpgradeAsync_InstallsSwitchesAndPreservesPreviousVersion()
    {
        using var sandbox = new TemporaryDirectory();
        const string previousVersion = "24.18.0";
        const string version = "24.19.0";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var previousDirectory = layout.GetRuntimeDirectory(RuntimeKind.Node, previousVersion);
        Directory.CreateDirectory(previousDirectory);
        await File.WriteAllTextAsync(Path.Combine(previousDirectory, "runtime.txt"), previousVersion);
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Node,
            previousVersion,
            RuntimeArchitecture.X64,
            previousDirectory,
            DateTimeOffset.UtcNow,
            true));
        var provider = new TestRuntimeProvider(RuntimeKind.Node, version);
        var links = new WindowsDirectoryLinkService(new ProcessRunner());
        var global = new GlobalRuntimeService(
            state,
            layout,
            links,
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator([provider], layout, state, global);

        try
        {
            await coordinator.UpgradeAsync(
                new RuntimeTarget(RuntimeKind.Node, version),
                makeCurrent: true);

            var installations = await state.GetInstallationsAsync();
            Assert.HasCount(2, installations);
            Assert.IsTrue(Directory.Exists(previousDirectory));
            Assert.IsFalse(installations.Single(item => item.Version == previousVersion).IsCurrent);
            Assert.IsTrue(installations.Single(item => item.Version == version).IsCurrent);
            var operation = (await state.GetOperationsAsync()).Single();
            Assert.AreEqual("upgrade", operation.Name);
            Assert.AreEqual(OperationStatus.Succeeded, operation.Status);
        }
        finally
        {
            links.Delete(layout.GetCurrentLink(RuntimeKind.Node));
        }
    }

    [TestMethod]
    public async Task InstallAsync_WhenCancellationArrivesAfterCommit_RecordsSucceeded()
    {
        using var sandbox = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        const string version = "1.2.3";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var state = new InMemoryStateStore
        {
            AfterUpsertInstallation = _ => cancellation.Cancel(),
        };
        var provider = new TestRuntimeProvider(RuntimeKind.Node, version);
        var progress = new ProgressRecorder();
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator([provider], layout, state, global);

        await coordinator.InstallAsync(
            new RuntimeTarget(RuntimeKind.Node, version),
            makeCurrent: false,
            progress,
            cancellationToken: cancellation.Token);

        Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.Node, version));
        Assert.IsTrue(Directory.Exists(Path.Combine(layout.AppDirectory, "node")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "java")));
        Assert.AreEqual(100, progress.Values.Last().Percentage);
        Assert.AreEqual(OperationStatus.Succeeded, (await state.GetOperationsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task InstallAsync_WhenMakeCurrentFails_RemovesInstallationAndFinalDirectory()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "1.2.3";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var state = new InMemoryStateStore();
        var provider = new TestRuntimeProvider(
            RuntimeKind.Node,
            version,
            prepare: async (directory, token) =>
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "runtime.txt"), version, token);
            },
            checkHealth: (directory, _) => Task.FromResult(
                directory.StartsWith(layout.CurrentDirectory, StringComparison.OrdinalIgnoreCase)
                    ? new RuntimeHealth(false, null, "simulated switch failure")
                    : new RuntimeHealth(true, version)));
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator([provider], layout, state, global);

        await Assert.ThrowsAsync<SoftPilot.Application.SoftPilotException>(() =>
            coordinator.InstallAsync(new RuntimeTarget(RuntimeKind.Node, version), makeCurrent: true));

        Assert.IsNull(await state.FindInstallationAsync(RuntimeKind.Node, version, includeDeleted: true));
        Assert.IsFalse(Directory.Exists(layout.GetRuntimeDirectory(RuntimeKind.Node, version)));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "node")));
        Assert.IsFalse(Directory.Exists(layout.GetCurrentLink(RuntimeKind.Node)));
        Assert.AreEqual(OperationStatus.Failed, (await state.GetOperationsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task InstallAsync_WhenSwitchRollbackFails_PreservesInstalledRuntime()
    {
        using var sandbox = new TemporaryDirectory();
        const string previousVersion = "1.0.0";
        const string version = "2.0.0";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var previousDirectory = layout.GetRuntimeDirectory(RuntimeKind.Node, previousVersion);
        Directory.CreateDirectory(previousDirectory);
        await File.WriteAllTextAsync(Path.Combine(previousDirectory, "previous.txt"), previousVersion);
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Node,
            previousVersion,
            RuntimeArchitecture.X64,
            previousDirectory,
            DateTimeOffset.UtcNow,
            true));
        var links = new WindowsDirectoryLinkService(new ProcessRunner());
        var linkPath = layout.GetCurrentLink(RuntimeKind.Node);
        await links.ReplaceAsync(linkPath, previousDirectory, CancellationToken.None);
        var provider = new TestRuntimeProvider(
            RuntimeKind.Node,
            version,
            prepare: async (directory, token) =>
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "runtime.txt"), version, token);
            },
            checkHealth: (directory, _) =>
            {
                if (directory.StartsWith(layout.CurrentDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(previousDirectory, recursive: true);
                    return Task.FromResult(new RuntimeHealth(false, null, "simulated switch failure"));
                }

                return Task.FromResult(new RuntimeHealth(true, version));
            });
        var global = new GlobalRuntimeService(
            state,
            layout,
            links,
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator([provider], layout, state, global);

        try
        {
            await Assert.ThrowsAsync<GlobalRuntimeRollbackException>(() =>
                coordinator.InstallAsync(new RuntimeTarget(RuntimeKind.Node, version), makeCurrent: true));

            Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.Node, version));
            Assert.IsTrue(Directory.Exists(layout.GetRuntimeDirectory(RuntimeKind.Node, version)));
            Assert.IsTrue(File.Exists(Path.Combine(linkPath, "runtime.txt")));
            Assert.AreEqual(OperationStatus.Failed, (await state.GetOperationsAsync()).Single().Status);
        }
        finally
        {
            links.Delete(linkPath);
        }
    }

    [TestMethod]
    public async Task UninstallAsync_PermanentlyDeletesRuntimeAndState()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "1.2.3";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Node, version);
        var archivePath = Path.Combine(layout.DownloadsDirectory, $"node-v{version}-win-x64.zip");
        var checksumPath = Path.Combine(layout.DownloadsDirectory, $"node-{version}-SHASUMS256.txt");
        var unrelatedCachePath = Path.Combine(layout.DownloadsDirectory, "unrelated.zip");
        Directory.CreateDirectory(installDirectory);
        await File.WriteAllTextAsync(Path.Combine(installDirectory, "runtime.txt"), version);
        await File.WriteAllTextAsync(archivePath, "archive");
        await File.WriteAllTextAsync(checksumPath, "checksums");
        await File.WriteAllTextAsync(checksumPath + ".sig", "signature");
        await File.WriteAllTextAsync(unrelatedCachePath, "preserve");
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Node,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Node, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator([provider], layout, state, global);

        await coordinator.UninstallAsync(new RuntimeTarget(RuntimeKind.Node, version));

        Assert.IsFalse(Directory.Exists(installDirectory));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(installDirectory)));
        Assert.IsFalse(File.Exists(archivePath));
        Assert.IsFalse(File.Exists(checksumPath));
        Assert.IsFalse(File.Exists(checksumPath + ".sig"));
        Assert.IsTrue(File.Exists(unrelatedCachePath));
        Assert.IsNull(await state.FindInstallationAsync(RuntimeKind.Node, version, includeDeleted: true));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(layout.StagingDirectory).Length);
        Assert.AreEqual(OperationStatus.Succeeded, (await state.GetOperationsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task UninstallAsync_WhenStateDeletionFails_RestoresRuntimeDirectory()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "1.2.3";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Node, version);
        var archivePath = Path.Combine(layout.DownloadsDirectory, $"node-v{version}-win-x64.zip");
        Directory.CreateDirectory(installDirectory);
        await File.WriteAllTextAsync(Path.Combine(installDirectory, "runtime.txt"), version);
        await File.WriteAllTextAsync(archivePath, "archive");
        var state = new InMemoryStateStore
        {
            BeforeDeleteInstallation = (_, _) => throw new InvalidOperationException("simulated state failure"),
        };
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Node,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Node, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator([provider], layout, state, global);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.UninstallAsync(new RuntimeTarget(RuntimeKind.Node, version)));

        Assert.IsTrue(Directory.Exists(installDirectory));
        Assert.IsTrue(File.Exists(archivePath));
        Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.Node, version));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(layout.StagingDirectory).Length);
        Assert.AreEqual(OperationStatus.Failed, (await state.GetOperationsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task UninstallAsync_WhenRuntimeFileIsInUse_PreservesRuntimeCacheAndState()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "1.2.3";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Node, version);
        var runtimePath = Path.Combine(installDirectory, "runtime.exe");
        var archivePath = Path.Combine(layout.DownloadsDirectory, $"node-v{version}-win-x64.zip");
        Directory.CreateDirectory(installDirectory);
        await File.WriteAllTextAsync(runtimePath, version);
        await File.WriteAllTextAsync(archivePath, "archive");
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Node,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Node, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator([provider], layout, state, global);
        await using var lockedRuntime = new FileStream(
            runtimePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var exception = await Assert.ThrowsAsync<SoftPilot.Application.SoftPilotException>(() =>
            coordinator.UninstallAsync(new RuntimeTarget(RuntimeKind.Node, version)));

        StringAssert.Contains(exception.Message, "正在使用");
        Assert.IsTrue(File.Exists(runtimePath));
        Assert.IsTrue(File.Exists(archivePath));
        Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.Node, version));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(layout.StagingDirectory).Length);
        Assert.AreEqual(OperationStatus.Failed, (await state.GetOperationsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task UninstallAsync_WhenRuntimeExecutableIsRunning_PreservesRuntimeCacheAndState()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "1.2.3";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Node, version);
        var runtimePath = Path.Combine(installDirectory, "runtime.exe");
        var archivePath = Path.Combine(layout.DownloadsDirectory, $"node-v{version}-win-x64.zip");
        Directory.CreateDirectory(installDirectory);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), runtimePath);
        await File.WriteAllTextAsync(archivePath, "archive");
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Node,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Node, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator([provider], layout, state, global);
        using var runningRuntime = Process.Start(new ProcessStartInfo(
            runtimePath,
            "-t 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new AssertFailedException("Unable to start test runtime process.");
        try
        {
            await Task.Delay(200);
            Assert.IsFalse(runningRuntime.HasExited);

            var exception = await Assert.ThrowsAsync<SoftPilot.Application.SoftPilotException>(() =>
                coordinator.UninstallAsync(new RuntimeTarget(RuntimeKind.Node, version)));

            StringAssert.Contains(exception.Message, "正在使用");
            Assert.IsTrue(File.Exists(runtimePath));
            Assert.IsTrue(File.Exists(archivePath));
            Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.Node, version));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(layout.StagingDirectory).Length);
            Assert.AreEqual(OperationStatus.Failed, (await state.GetOperationsAsync()).Single().Status);
        }
        finally
        {
            if (!runningRuntime.HasExited)
            {
                runningRuntime.Kill(entireProcessTree: true);
                await runningRuntime.WaitForExitAsync();
            }
        }
    }

    [TestMethod]
    public async Task UninstallAsync_RedisPreservesDataAndLogsByDefault()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "8.2.9";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Redis, version);
        var dataDirectory = layout.GetRedisDataDirectory(version);
        var logPath = layout.GetRedisLogPath(version);
        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "dump.rdb"), "data");
        await File.WriteAllTextAsync(logPath, "log");
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Redis,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Redis, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator(
            [provider],
            layout,
            state,
            global,
            StoppedRedis);

        await coordinator.UninstallAsync(new RuntimeTarget(RuntimeKind.Redis, version));

        Assert.IsFalse(Directory.Exists(installDirectory));
        Assert.IsTrue(File.Exists(Path.Combine(dataDirectory, "dump.rdb")));
        Assert.IsTrue(File.Exists(logPath));
    }

    [TestMethod]
    public async Task UninstallAsync_RedisDeleteDataOptionDeletesDataAndLogs()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "8.2.9";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Redis, version);
        var dataDirectory = layout.GetRedisDataDirectory(version);
        var logDirectory = Path.GetDirectoryName(layout.GetRedisLogPath(version))!;
        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "dump.rdb"), "data");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "redis.log"), "log");
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Redis,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Redis, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator(
            [provider],
            layout,
            state,
            global,
            StoppedRedis);

        await coordinator.UninstallAsync(
            new RuntimeTarget(RuntimeKind.Redis, version),
            new RuntimeUninstallOptions(DeleteData: true));

        Assert.IsFalse(Directory.Exists(installDirectory));
        Assert.IsFalse(Directory.Exists(dataDirectory));
        Assert.IsFalse(Directory.Exists(logDirectory));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(layout.StagingDirectory).Length);
    }

    [TestMethod]
    public async Task UninstallAsync_RedisRemovesStoppedServiceState()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "8.2.9";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Redis, version);
        var serviceStatePath = layout.GetRedisServiceStatePath();
        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(serviceStatePath)!);
        await File.WriteAllTextAsync(serviceStatePath, "stale state");
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Redis,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Redis, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var stoppedRedis = new StubRedisServiceManager(new RedisServiceStatus(false, version));
        var coordinator = new OperationCoordinator(
            [provider],
            layout,
            state,
            global,
            stoppedRedis);

        await coordinator.UninstallAsync(new RuntimeTarget(RuntimeKind.Redis, version));

        Assert.IsFalse(File.Exists(serviceStatePath));
        Assert.IsFalse(Directory.Exists(installDirectory));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(layout.StagingDirectory).Length);
    }

    [TestMethod]
    public async Task UninstallAsync_WhenRedisStateDeletionFails_RestoresRuntimeDataAndLogs()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "8.2.9";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Redis, version);
        var dataPath = Path.Combine(layout.GetRedisDataDirectory(version), "dump.rdb");
        var logPath = layout.GetRedisLogPath(version);
        var serviceStatePath = layout.GetRedisServiceStatePath();
        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(dataPath, "data");
        await File.WriteAllTextAsync(logPath, "log");
        await File.WriteAllTextAsync(serviceStatePath, "stale state");
        var state = new InMemoryStateStore
        {
            BeforeDeleteInstallation = (_, _) => throw new InvalidOperationException("simulated state failure"),
        };
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Redis,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Redis, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var coordinator = new OperationCoordinator(
            [provider],
            layout,
            state,
            global,
            new StubRedisServiceManager(new RedisServiceStatus(false, version)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.UninstallAsync(
            new RuntimeTarget(RuntimeKind.Redis, version),
            new RuntimeUninstallOptions(DeleteData: true)));

        Assert.IsTrue(Directory.Exists(installDirectory));
        Assert.IsTrue(File.Exists(dataPath));
        Assert.IsTrue(File.Exists(logPath));
        Assert.IsTrue(File.Exists(serviceStatePath));
        Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.Redis, version));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(layout.StagingDirectory).Length);
    }

    [TestMethod]
    public async Task UninstallAsync_WhenRedisVersionIsRunning_PreservesRuntimeAndState()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "8.2.9";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.Redis, version);
        Directory.CreateDirectory(installDirectory);
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Redis,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.Redis, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var redis = new StubRedisServiceManager(new RedisServiceStatus(true, version, 1234));
        var coordinator = new OperationCoordinator([provider], layout, state, global, redis);

        var exception = await Assert.ThrowsAsync<SoftPilot.Application.SoftPilotException>(() =>
            coordinator.UninstallAsync(new RuntimeTarget(RuntimeKind.Redis, version)));

        StringAssert.Contains(exception.Message, "正在运行");
        Assert.IsTrue(Directory.Exists(installDirectory));
        Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.Redis, version));
    }

    [TestMethod]
    public async Task UninstallAsync_WhenMySqlVersionIsRunning_PreservesRuntimeAndState()
    {
        using var sandbox = new TemporaryDirectory();
        const string version = "8.4.11";
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var installDirectory = layout.GetRuntimeDirectory(RuntimeKind.MySql, version);
        Directory.CreateDirectory(installDirectory);
        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.MySql,
            version,
            RuntimeArchitecture.X64,
            installDirectory,
            DateTimeOffset.UtcNow,
            false));
        var provider = new TestRuntimeProvider(RuntimeKind.MySql, version);
        var global = new GlobalRuntimeService(
            state,
            layout,
            new WindowsDirectoryLinkService(new ProcessRunner()),
            [provider],
            new TestShellIntegrationService());
        var mysql = new StubMySqlServiceManager(new MySqlServiceStatus(true, version, 1234));
        var coordinator = new OperationCoordinator(
            [provider],
            layout,
            state,
            global,
            mySqlServices: mysql);

        var exception = await Assert.ThrowsAsync<SoftPilot.Application.SoftPilotException>(() =>
            coordinator.UninstallAsync(new RuntimeTarget(RuntimeKind.MySql, version)));

        StringAssert.Contains(exception.Message, "正在运行");
        Assert.IsTrue(Directory.Exists(installDirectory));
        Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.MySql, version));
    }

    private sealed class ProgressRecorder : IProgress<OperationProgress>
    {
        public List<OperationProgress> Values { get; } = [];

        public void Report(OperationProgress value) => Values.Add(value);
    }

    private sealed class StubRedisServiceManager(RedisServiceStatus status) : IRedisServiceManager
    {
        public Task<RedisServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<RedisServiceStatus> StartAsync(string version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubMySqlServiceManager(MySqlServiceStatus status) : IMySqlServiceManager
    {
        public Task<IReadOnlyList<MySqlServiceStatus>> GetStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MySqlServiceStatus>>([status]);

        public Task<MySqlServiceStatus> GetStatusAsync(string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<MySqlServiceStatus> StartAsync(string version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(string version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MySqlCredentials> GetCredentialsAsync(string version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int GetConfiguredPort(string version) => status.Port;

        public Task SetConfiguredPortAsync(string version, int port, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
