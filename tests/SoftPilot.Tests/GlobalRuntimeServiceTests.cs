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
        var service = new GlobalRuntimeService(state, layout, links, [provider]);
        return new Context(service, state, links, linkPath, previousVersion, nextVersion);
    }

    private sealed record Context(
        GlobalRuntimeService Service,
        InMemoryStateStore State,
        WindowsDirectoryLinkService Links,
        string LinkPath,
        string PreviousVersion,
        string NextVersion);
}
