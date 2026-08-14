using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Tests;

[TestClass]
public sealed class OperationCoordinatorTests
{
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
        Directory.CreateDirectory(installDirectory);
        await File.WriteAllTextAsync(Path.Combine(installDirectory, "runtime.txt"), version);
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
        Directory.CreateDirectory(installDirectory);
        await File.WriteAllTextAsync(Path.Combine(installDirectory, "runtime.txt"), version);
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
        Assert.IsNotNull(await state.FindInstallationAsync(RuntimeKind.Node, version));
        Assert.AreEqual(OperationStatus.Failed, (await state.GetOperationsAsync()).Single().Status);
    }

    private sealed class ProgressRecorder : IProgress<OperationProgress>
    {
        public List<OperationProgress> Values { get; } = [];

        public void Report(OperationProgress value) => Values.Add(value);
    }
}
