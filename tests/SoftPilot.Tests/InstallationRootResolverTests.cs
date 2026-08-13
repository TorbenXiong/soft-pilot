using SoftPilot.Application;

namespace SoftPilot.Tests;

[TestClass]
public sealed class InstallationRootResolverTests
{
    [TestMethod]
    [DataRow(@"D:\", @"D:\SoftPilot")]
    [DataRow(@"D:\DevTools", @"D:\DevTools\SoftPilot")]
    [DataRow(@"D:\SoftPilot", @"D:\SoftPilot")]
    [DataRow(@"D:\softpilot", @"D:\softpilot\SoftPilot")]
    [DataRow(@"D:\SOFTPILOT", @"D:\SOFTPILOT\SoftPilot")]
    [DataRow(@"D:\DevTools\SoftPilot", @"D:\DevTools\SoftPilot")]
    [DataRow(@"D:\包含 空格", @"D:\包含 空格\SoftPilot")]
    [DataRow(@"D:\DevTools\.\SoftPilot\", @"D:\DevTools\SoftPilot")]
    [DataRow(@"D:\DevTools\child\..", @"D:\DevTools\SoftPilot")]
    public void Resolve_UsesOrdinalCaseSensitiveFinalComponent(string selected, string expected)
    {
        Assert.AreEqual(expected, InstallationRootResolver.Resolve(selected));
    }
}
