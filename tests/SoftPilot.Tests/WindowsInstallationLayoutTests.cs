using SoftPilot.Infrastructure.Installation;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WindowsInstallationLayoutTests
{
    [TestMethod]
    public void EnsureWorkspace_DoesNotCreateRuntimeKindDirectoriesBeforeInstall()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);

        layout.EnsureWorkspace();

        Assert.AreEqual(Path.Combine(sandbox.Path, "SoftPilotData"), layout.ManagementDirectory);
        Assert.AreEqual(Path.Combine(layout.ManagementDirectory, "tools"), layout.ToolsDirectory);
        Assert.AreEqual(Path.Combine(layout.ToolsDirectory, "shims"), layout.ShimsDirectory);
        Assert.IsTrue(Directory.Exists(layout.AppDirectory));
        Assert.IsTrue(File.Exists(Path.Combine(layout.ManagementDirectory, ".softpilot-root")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "node")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "java")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "python")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "redis")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "mysql")));
        Assert.AreEqual(
            Path.Combine(layout.DataDirectory, "redis", "8.2.9"),
            layout.GetRedisDataDirectory("8.2.9"));
        Assert.AreEqual(
            Path.Combine(layout.DataDirectory, "mysql", "5.7", "data"),
            layout.GetMySqlDataDirectory("5.7.44"));
        Assert.AreEqual(
            Path.Combine(layout.DataDirectory, "mysql", "8.4", "my.ini"),
            layout.GetMySqlConfigPath("8.4.11"));
    }
}
