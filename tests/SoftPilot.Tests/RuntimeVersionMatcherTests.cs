using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Tests;

[TestClass]
public sealed class RuntimeVersionMatcherTests
{
    [TestMethod]
    [DataRow("24.19.0", "v24.19.0")]
    [DataRow("21.0.12+8.0.LTS", "21.0.12")]
    [DataRow("8.0.502+7", "1.8.0_502")]
    [DataRow("8.0.502+7", "1.8.0_502-b08")]
    [DataRow("3.14.7", "3.14.7")]
    public void AreEquivalent_AcceptsProviderHealthVersionFormats(string expected, string actual)
    {
        Assert.IsTrue(RuntimeVersionMatcher.AreEquivalent(expected, actual));
    }

    [TestMethod]
    public void AreEquivalent_RejectsDifferentVersions()
    {
        Assert.IsFalse(RuntimeVersionMatcher.AreEquivalent("24.19.0", "22.23.2"));
        Assert.IsFalse(RuntimeVersionMatcher.AreEquivalent("8.0.502+7", "1.8.0_492"));
    }
}
