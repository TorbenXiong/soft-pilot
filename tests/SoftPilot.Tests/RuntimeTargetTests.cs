using SoftPilot.Domain;

namespace SoftPilot.Tests;

[TestClass]
public sealed class RuntimeTargetTests
{
    [TestMethod]
    [DataRow("node@24.13.0", RuntimeKind.Node, "24.13.0")]
    [DataRow("JAVA@21.0.8+9", RuntimeKind.Java, "21.0.8+9")]
    [DataRow("python@3.14.6", RuntimeKind.Python, "3.14.6")]
    [DataRow("redis@8.2.9", RuntimeKind.Redis, "8.2.9")]
    public void TryParse_AcceptsExactTargets(string value, RuntimeKind kind, string version)
    {
        Assert.IsTrue(RuntimeTarget.TryParse(value, out var target));
        Assert.AreEqual(kind, target.Kind);
        Assert.AreEqual(version, target.Version);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("node")]
    [DataRow("node@")]
    [DataRow("ruby@3.4.0")]
    [DataRow("node@24 @")]
    public void TryParse_RejectsInvalidTargets(string value)
    {
        Assert.IsFalse(RuntimeTarget.TryParse(value, out _));
    }
}
