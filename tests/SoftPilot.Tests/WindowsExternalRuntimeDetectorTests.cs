using SoftPilot.Domain;
using SoftPilot.Infrastructure.Detection;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WindowsExternalRuntimeDetectorTests
{
    [TestMethod]
    [DataRow(RuntimeKind.Node, "v24.19.0", "24.19.0")]
    [DataRow(RuntimeKind.Node, "v26.0.0-rc.1", "26.0.0-rc.1")]
    [DataRow(RuntimeKind.Python, "Python 3.14.7", "3.14.7")]
    [DataRow(RuntimeKind.Java, "openjdk version \"21.0.12\" 2026-07-21 LTS", "21.0.12")]
    public void ParseVersion_ReturnsValidRuntimeVersion(RuntimeKind kind, string output, string expected)
    {
        Assert.AreEqual(expected, WindowsExternalRuntimeDetector.ParseVersion(kind, output));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("********************************************************************************")]
    [DataRow("Python was not found")]
    public void ParseVersion_RejectsNonVersionOutput(string output)
    {
        Assert.IsNull(WindowsExternalRuntimeDetector.ParseVersion(RuntimeKind.Python, output));
    }

    [TestMethod]
    public void WindowsAppsDirectory_IsRecognizedAsAppAliasLocation()
    {
        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");

        Assert.IsTrue(WindowsExternalRuntimeDetector.IsWindowsAppsDirectory(windowsApps));
        Assert.IsTrue(WindowsExternalRuntimeDetector.IsWindowsAppsDirectory(Path.Combine(windowsApps, "PythonManager_package")));
        Assert.IsFalse(WindowsExternalRuntimeDetector.IsWindowsAppsDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Python")));
    }
}
