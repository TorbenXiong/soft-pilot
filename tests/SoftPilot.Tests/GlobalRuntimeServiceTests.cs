using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Tests;

[TestClass]
public sealed class GlobalRuntimeServiceTests
{
    [TestMethod]
    public async Task UseAsync_WhenHealthCheckFails_RestoresPreviousLinkAndState()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(false, null, "simulated failure")));
        try
        {
            await Assert.ThrowsAsync<SoftPilot.Application.SoftPilotException>(() =>
                context.Service.UseAsync(RuntimeKind.Node, context.NextVersion));

            Assert.IsTrue(File.Exists(Path.Combine(context.LinkPath, "previous.txt")));
            Assert.AreEqual(context.PreviousVersion, (await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.PreviousVersion))?.Version);
            Assert.IsTrue((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.PreviousVersion))!.IsCurrent);
            Assert.IsFalse((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.NextVersion))!.IsCurrent);
        }
        finally
        {
            context.Links.Delete(context.LinkPath);
        }
    }

    [TestMethod]
    public async Task UseAsync_WhenCancelledDuringHealthCheck_RestoresPreviousLinkAndState()
    {
        using var sandbox = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var context = await CreateContextAsync(sandbox.Path, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeHealth(true, "2.0.0"));
        });

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                context.Service.UseAsync(RuntimeKind.Node, context.NextVersion, cancellation.Token));

            Assert.IsTrue(File.Exists(Path.Combine(context.LinkPath, "previous.txt")));
            Assert.IsTrue((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.PreviousVersion))!.IsCurrent);
            Assert.IsFalse((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.NextVersion))!.IsCurrent);
        }
        finally
        {
            context.Links.Delete(context.LinkPath);
        }
    }

    [TestMethod]
    public async Task ClearAsync_WhenStateUpdateFails_RestoresPreviousLink()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(true, "2.0.0")));
        context.State.SetCurrentFailure = (_, version) => version is null
            ? new InvalidOperationException("simulated database failure")
            : null;

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Service.ClearAsync(RuntimeKind.Node));

            Assert.IsTrue(File.Exists(Path.Combine(context.LinkPath, "previous.txt")));
            Assert.IsTrue((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.PreviousVersion))!.IsCurrent);
        }
        finally
        {
            context.Links.Delete(context.LinkPath);
        }
    }

    [TestMethod]
    public async Task ClearAsync_RemovesCurrentLinkAndClearsState()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(true, "2.0.0")));

        await context.Service.ClearAsync(RuntimeKind.Node);

        Assert.IsFalse(Directory.Exists(context.LinkPath));
        Assert.IsFalse((await context.State
            .FindInstallationAsync(RuntimeKind.Node, context.PreviousVersion))!.IsCurrent);
        Assert.IsNull((await context.Service.GetCurrentAsync())[RuntimeKind.Node]);
        Assert.AreEqual(1, context.Shell.DisableCalls);
        Assert.IsFalse(context.Shell.IsEnabled);
    }

    [TestMethod]
    public async Task UseAsync_WhenShellIntegrationIsDisabled_EnablesItAutomatically()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(true, "2.0.0")));
        context.Shell.IsEnabled = false;

        try
        {
            await context.Service.UseAsync(RuntimeKind.Node, context.NextVersion);

            Assert.AreEqual(1, context.Shell.EnableCalls);
            Assert.IsTrue(context.Shell.IsEnabled);
            Assert.IsTrue((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.NextVersion))!.IsCurrent);
        }
        finally
        {
            context.Links.Delete(context.LinkPath);
        }
    }

    [TestMethod]
    public async Task UseAsync_WhenShellEnableFails_RestoresPreviousSelection()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(true, "2.0.0")));
        context.Shell.IsEnabled = false;
        context.Shell.EnableFailure = new InvalidOperationException("simulated shell failure");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Service.UseAsync(RuntimeKind.Node, context.NextVersion));

            Assert.AreEqual(1, context.Shell.DisableCalls);
            Assert.IsTrue((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.PreviousVersion))!.IsCurrent);
            Assert.IsFalse((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.NextVersion))!.IsCurrent);
            Assert.IsTrue(File.Exists(Path.Combine(context.LinkPath, "previous.txt")));
        }
        finally
        {
            context.Links.Delete(context.LinkPath);
        }
    }

    [TestMethod]
    public async Task ClearAsync_WhenAnotherRuntimeIsCurrent_KeepsShellIntegrationEnabled()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(true, "2.0.0")));
        var javaDirectory = Path.Combine(sandbox.Path, "java-current");
        Directory.CreateDirectory(javaDirectory);
        await context.State.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Java,
            "21.0.1",
            RuntimeArchitecture.X64,
            javaDirectory,
            DateTimeOffset.UtcNow,
            true));

        await context.Service.ClearAsync(RuntimeKind.Node);

        Assert.AreEqual(0, context.Shell.DisableCalls);
        Assert.IsTrue(context.Shell.IsEnabled);
    }

    [TestMethod]
    public async Task ClearAsync_WhenShellDisableFails_RestoresPreviousSelection()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(true, "2.0.0")));
        context.Shell.DisableFailure = new InvalidOperationException("simulated shell failure");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Service.ClearAsync(RuntimeKind.Node));

            Assert.AreEqual(1, context.Shell.EnableCalls);
            Assert.IsTrue((await context.State
                .FindInstallationAsync(RuntimeKind.Node, context.PreviousVersion))!.IsCurrent);
            Assert.IsTrue(File.Exists(Path.Combine(context.LinkPath, "previous.txt")));
        }
        finally
        {
            context.Links.Delete(context.LinkPath);
        }
    }

    [TestMethod]
    public async Task ReconcileShellIntegrationAsync_WhenCurrentVersionExists_EnablesShell()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(true, "2.0.0")));
        context.Shell.IsEnabled = false;

        try
        {
            await context.Service.ReconcileShellIntegrationAsync();

            Assert.AreEqual(1, context.Shell.EnableCalls);
            Assert.IsTrue(context.Shell.IsEnabled);
        }
        finally
        {
            context.Links.Delete(context.LinkPath);
        }
    }

    [TestMethod]
    public async Task ReconcileShellIntegrationAsync_WhenNoCurrentVersionExists_DisablesShell()
    {
        using var sandbox = new TemporaryDirectory();
        var context = await CreateContextAsync(sandbox.Path, (_, _) =>
            Task.FromResult(new RuntimeHealth(true, "2.0.0")));
        await context.State.SetCurrentAsync(RuntimeKind.Node, null);

        try
        {
            await context.Service.ReconcileShellIntegrationAsync();

            Assert.AreEqual(1, context.Shell.DisableCalls);
            Assert.IsFalse(context.Shell.IsEnabled);
        }
        finally
        {
            context.Links.Delete(context.LinkPath);
        }
    }

    private static async Task<Context> CreateContextAsync(
        string root,
        Func<string, CancellationToken, Task<RuntimeHealth>> checkHealth)
    {
        const string previousVersion = "1.0.0";
        const string nextVersion = "2.0.0";
        var layout = new WindowsInstallationLayout(root);
        layout.EnsureWorkspace();
        var previousDirectory = layout.GetRuntimeDirectory(RuntimeKind.Node, previousVersion);
        var nextDirectory = layout.GetRuntimeDirectory(RuntimeKind.Node, nextVersion);
        Directory.CreateDirectory(previousDirectory);
        Directory.CreateDirectory(nextDirectory);
        await File.WriteAllTextAsync(Path.Combine(previousDirectory, "previous.txt"), "previous");
        await File.WriteAllTextAsync(Path.Combine(nextDirectory, "next.txt"), "next");

        var state = new InMemoryStateStore();
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Node,
            previousVersion,
            RuntimeArchitecture.X64,
            previousDirectory,
            DateTimeOffset.UtcNow,
            true));
        await state.UpsertInstallationAsync(new RuntimeInstallation(
            RuntimeKind.Node,
            nextVersion,
            RuntimeArchitecture.X64,
            nextDirectory,
            DateTimeOffset.UtcNow,
            false));

        var links = new WindowsDirectoryLinkService(new ProcessRunner());
        var linkPath = layout.GetCurrentLink(RuntimeKind.Node);
        await links.ReplaceAsync(linkPath, previousDirectory, CancellationToken.None);
        var provider = new TestRuntimeProvider(RuntimeKind.Node, nextVersion, checkHealth: checkHealth);
        var shell = new TestShellIntegrationService { IsEnabled = true };
        var service = new GlobalRuntimeService(state, layout, links, [provider], shell);
        return new Context(service, state, links, shell, linkPath, previousVersion, nextVersion);
    }

    private sealed record Context(
        GlobalRuntimeService Service,
        InMemoryStateStore State,
        WindowsDirectoryLinkService Links,
        TestShellIntegrationService Shell,
        string LinkPath,
        string PreviousVersion,
        string NextVersion);
}
