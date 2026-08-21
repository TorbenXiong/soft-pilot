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
    [DataRow(RuntimeKind.Redis, "Redis server v=8.2.9 sha=00000000:0 malloc=jemalloc bits=64", "8.2.9")]
    [DataRow(RuntimeKind.MySql, "mysqld  Ver 8.4.11 for Win64 on x86_64 (MySQL Community Server - GPL)", "8.4.11")]
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

    [TestMethod]
    public void ManagedWorkspacePaths_AreExcludedFromExternalDiscovery()
    {
        var managementDirectory = Path.Combine("D:\\SoftPilot", "SoftPilotData");

        Assert.IsTrue(WindowsExternalRuntimeDetector.IsPathUnderDirectory(
            Path.Combine(managementDirectory, "tools", "shims", "node.exe"),
            managementDirectory));
        Assert.IsTrue(WindowsExternalRuntimeDetector.IsPathUnderDirectory(
            Path.Combine(managementDirectory, "current", "node", "node.exe"),
            managementDirectory));
        Assert.IsFalse(WindowsExternalRuntimeDetector.IsPathUnderDirectory(
            Path.Combine("D:\\External", "nodejs", "node.exe"),
            managementDirectory));
        Assert.IsFalse(WindowsExternalRuntimeDetector.IsPathUnderDirectory(
            Path.Combine("D:\\SoftPilotDataBackup", "node.exe"),
            managementDirectory));
        Assert.IsFalse(WindowsExternalRuntimeDetector.IsPathUnderDirectory(
            "invalid\0path",
            managementDirectory));
    }

    [TestMethod]
    public void SoftPilotManagedRuntimePaths_AreExcludedAcrossPortableRoots()
    {
        Assert.IsTrue(WindowsExternalRuntimeDetector.IsSoftPilotManagedRuntimePath(
            @"D:\SoftPilot\SoftPilotData\tools\shims\node.exe"));
        Assert.IsTrue(WindowsExternalRuntimeDetector.IsSoftPilotManagedRuntimePath(
            @"D:\SoftPilot\SoftPilotData\current\node\node.exe"));
        Assert.IsTrue(WindowsExternalRuntimeDetector.IsSoftPilotManagedRuntimePath(
            @"E:\Portable\SoftPilotData\app\node\24.19.0\node.exe"));
        Assert.IsTrue(WindowsExternalRuntimeDetector.IsSoftPilotManagedRuntimePath(
            @"E:\Portable\SOFTPILOTDATA\TOOLS\shims\node.exe"));
        Assert.IsFalse(WindowsExternalRuntimeDetector.IsSoftPilotManagedRuntimePath(
            @"D:\External\nodejs\node.exe"));
        Assert.IsFalse(WindowsExternalRuntimeDetector.IsSoftPilotManagedRuntimePath(
            @"D:\SoftPilotDataBackup\node.exe"));
    }
}
