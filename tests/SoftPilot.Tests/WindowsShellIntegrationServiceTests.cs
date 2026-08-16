using SoftPilot.Infrastructure.Shell;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WindowsShellIntegrationServiceTests
{
    [TestMethod]
    public void BuildEnabledPath_PutsShimsAndCurrentNodeFirstWithoutDuplicates()
    {
        var result = WindowsShellIntegrationService.BuildEnabledPath(
            @"C:\Tools;D:\SoftPilot\SoftPilotData\current\node;D:\SoftPilot\SoftPilotData\tools\shims;C:\Windows",
            @"D:\SoftPilot\SoftPilotData\tools\shims",
            @"D:\SoftPilot\SoftPilotData\current\node");

        CollectionAssert.AreEqual(
            new[]
            {
                @"D:\SoftPilot\SoftPilotData\tools\shims",
                @"D:\SoftPilot\SoftPilotData\current\node",
                @"C:\Tools",
                @"C:\Windows",
            },
            result.Split(Path.PathSeparator));
    }

    [TestMethod]
    public void BuildDisabledPath_RemovesAllManagedEntries()
    {
        var result = WindowsShellIntegrationService.BuildDisabledPath(
            @"D:\SoftPilot\SoftPilotData\tools\shims;C:\Tools;D:\SoftPilot\SoftPilotData\current\node;D:\SOFTPILOT\SOFTPILOTDATA\TOOLS\SHIMS",
            @"D:\SoftPilot\SoftPilotData\tools\shims",
            @"D:\SoftPilot\SoftPilotData\current\node");

        Assert.AreEqual(@"C:\Tools", result);
    }
}
