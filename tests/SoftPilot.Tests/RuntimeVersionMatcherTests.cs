using SoftPilot.Infrastructure.Runtime;
using SoftPilot.Domain;

namespace SoftPilot.Tests;

[TestClass]
public sealed class RuntimeVersionMatcherTests
{
    [TestMethod]
    [DataRow(RuntimeKind.Node, "24.19.0", "v24.19.0")]
    [DataRow(RuntimeKind.Java, "21.0.12+8.0.LTS", "21.0.12")]
    [DataRow(RuntimeKind.Java, "25.0.4+101.0.LTS", "25.0.4.1")]
    [DataRow(RuntimeKind.Java, "8.0.502+7", "1.8.0_502")]
    [DataRow(RuntimeKind.Java, "8.0.502+7", "1.8.0_502-b08")]
    [DataRow(RuntimeKind.Python, "3.14.7", "3.14.7")]
    public void AreEquivalent_AcceptsProviderHealthVersionFormats(
        RuntimeKind kind,
        string expected,
        string actual)
    {
        Assert.IsTrue(RuntimeVersionMatcher.AreEquivalent(kind, expected, actual));
    }

    [TestMethod]
    public void AreEquivalent_RejectsDifferentVersions()
    {
        Assert.IsFalse(RuntimeVersionMatcher.AreEquivalent(RuntimeKind.Node, "24.19.0", "22.23.2"));
        Assert.IsFalse(RuntimeVersionMatcher.AreEquivalent(RuntimeKind.Java, "8.0.502+7", "1.8.0_492"));
    }
}
