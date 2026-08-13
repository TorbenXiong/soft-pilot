using SoftPilot.Domain;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.State;

namespace SoftPilot.Tests;

[TestClass]
public sealed class SqliteStateStoreTests
{
    [TestMethod]
    public async Task InstallationLifecycle_PersistsCurrentDeleteAndRestore()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var store = new SqliteStateStore(layout);
        await store.InitializeAsync();

        var path = layout.GetRuntimeDirectory(RuntimeKind.Node, "24.13.0");
        var installation = new RuntimeInstallation(
            RuntimeKind.Node,
            "24.13.0",
            RuntimeArchitecture.X64,
            path,
            DateTimeOffset.UtcNow,
            false);
        await store.UpsertInstallationAsync(installation);
        await store.SetCurrentAsync(RuntimeKind.Node, installation.Version);

        var current = await store.FindInstallationAsync(RuntimeKind.Node, installation.Version);
        Assert.IsNotNull(current);
        Assert.IsTrue(current.IsCurrent);

        var trash = layout.GetTrashDirectory(RuntimeKind.Node, installation.Version, DateTimeOffset.UtcNow);
        await store.MarkDeletedAsync(RuntimeKind.Node, installation.Version, DateTimeOffset.UtcNow, trash);
        Assert.IsNull(await store.FindInstallationAsync(RuntimeKind.Node, installation.Version));

        var deleted = await store.FindInstallationAsync(RuntimeKind.Node, installation.Version, includeDeleted: true);
        Assert.IsNotNull(deleted);
        Assert.IsTrue(deleted.IsDeleted);

        await store.RestoreAsync(RuntimeKind.Node, installation.Version, path);
        var restored = await store.FindInstallationAsync(RuntimeKind.Node, installation.Version);
        Assert.IsNotNull(restored);
        Assert.IsFalse(restored.IsDeleted);
    }
}
