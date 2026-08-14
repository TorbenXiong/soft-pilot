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

        Assert.IsTrue(Directory.Exists(layout.AppDirectory));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "node")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "java")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.AppDirectory, "python")));
    }
}
