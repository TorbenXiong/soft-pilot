using SoftPilot.Application;
using SoftPilot.Infrastructure.Installation;

namespace SoftPilot.Tests;

[TestClass]
public sealed class PortableAppMigratorTests
{
    [TestMethod]
    public async Task MigrateAsync_CopiesOnlyExecutableAndPreservesManagementData()
    {
        using var sandbox = new TemporaryDirectory();
        var sourceRoot = Path.Combine(sandbox.Path, "source");
        var targetRoot = Path.Combine(sandbox.Path, "target");
        Directory.CreateDirectory(sourceRoot);
        var source = Path.Combine(sourceRoot, "downloaded.exe");
        var target = Path.Combine(targetRoot, "SoftPilot.exe");
        File.WriteAllText(source, "app body");
        File.WriteAllText(Path.Combine(sourceRoot, "unrelated.txt"), "keep at source");
        Directory.CreateDirectory(Path.Combine(targetRoot, "SoftPilotData", "data"));
        File.WriteAllText(Path.Combine(targetRoot, "SoftPilotData", "data", "state.txt"), "preserve");

        await new PortableAppMigrator().MigrateAsync(source, target);

        Assert.AreEqual("app body", File.ReadAllText(target));
        Assert.AreEqual(
            "preserve",
            File.ReadAllText(Path.Combine(targetRoot, "SoftPilotData", "data", "state.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(targetRoot, "unrelated.txt")));
    }

    [TestMethod]
    public async Task MigrateAsync_ReplacesExistingExecutable()
    {
        using var sandbox = new TemporaryDirectory();
        var source = Path.Combine(sandbox.Path, "new.exe");
        var targetRoot = Path.Combine(sandbox.Path, "SoftPilot");
        var target = Path.Combine(targetRoot, "SoftPilot.exe");
        Directory.CreateDirectory(targetRoot);
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");

        await new PortableAppMigrator().MigrateAsync(source, target);

        Assert.AreEqual("new", File.ReadAllText(target));
        Assert.AreEqual(0, Directory.GetFiles(targetRoot, ".SoftPilot.previous-*.exe").Length);
    }

    [TestMethod]
    public async Task CleanupSourceExecutableAsync_DeletesOnlySourceExecutable()
    {
        using var sandbox = new TemporaryDirectory();
        var sourceRoot = Path.Combine(sandbox.Path, "source");
        var targetRoot = Path.Combine(sandbox.Path, "target");
        Directory.CreateDirectory(sourceRoot);
        var source = Path.Combine(sourceRoot, "downloaded.exe");
        var target = Path.Combine(targetRoot, "SoftPilot.exe");
        File.WriteAllText(source, "app body");
        File.WriteAllText(Path.Combine(sourceRoot, "unrelated.txt"), "keep");
        await new PortableAppMigrator().MigrateAsync(source, target);

        await new PortableAppMigrator().CleanupSourceExecutableAsync(source, target);

        Assert.IsFalse(File.Exists(source));
        Assert.IsTrue(File.Exists(Path.Combine(sourceRoot, "unrelated.txt")));
        Assert.IsTrue(File.Exists(target));
    }

    [TestMethod]
    public async Task CleanupSourceExecutableAsync_WhenSourceChanged_PreservesSource()
    {
        using var sandbox = new TemporaryDirectory();
        var source = Path.Combine(sandbox.Path, "source.exe");
        var target = Path.Combine(sandbox.Path, "target", "SoftPilot.exe");
        File.WriteAllText(source, "app body");
        await new PortableAppMigrator().MigrateAsync(source, target);
        File.AppendAllText(source, "changed");

        await Assert.ThrowsExactlyAsync<SoftPilotException>(() =>
            new PortableAppMigrator().CleanupSourceExecutableAsync(source, target));

        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task MigrateAsync_AllowsCanonicalRenameInSameDirectory()
    {
        using var sandbox = new TemporaryDirectory();
        var sourceRoot = Path.Combine(sandbox.Path, "source");
        Directory.CreateDirectory(sourceRoot);
        var source = Path.Combine(sourceRoot, "SoftPilot-Portable.exe");
        var target = Path.Combine(sourceRoot, "SoftPilot.exe");
        File.WriteAllText(source, "app body");

        await new PortableAppMigrator().MigrateAsync(source, target);

        Assert.AreEqual("app body", File.ReadAllText(target));
        Assert.IsTrue(File.Exists(source));
    }
}
