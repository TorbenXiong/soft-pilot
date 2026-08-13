using SoftPilot.Infrastructure.Shell;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WindowsShellIntegrationServiceTests
{
    [TestMethod]
    public void BuildEnabledPath_PutsShimsAndCurrentNodeFirstWithoutDuplicates()
    {
        var result = WindowsShellIntegrationService.BuildEnabledPath(
            @"C:\Tools;D:\SoftPilot\current\node;D:\SoftPilot\bin\shims;C:\Windows",
            @"D:\SoftPilot\bin\shims",
            @"D:\SoftPilot\current\node");

        CollectionAssert.AreEqual(
            new[]
            {
                @"D:\SoftPilot\bin\shims",
                @"D:\SoftPilot\current\node",
                @"C:\Tools",
                @"C:\Windows",
            },
            result.Split(Path.PathSeparator));
    }

    [TestMethod]
    public void BuildDisabledPath_RemovesAllManagedEntries()
    {
        var result = WindowsShellIntegrationService.BuildDisabledPath(
            @"D:\SoftPilot\bin\shims;C:\Tools;D:\SoftPilot\current\node;D:\SOFTPILOT\BIN\SHIMS",
            @"D:\SoftPilot\bin\shims",
            @"D:\SoftPilot\current\node");

        Assert.AreEqual(@"C:\Tools", result);
    }
}
