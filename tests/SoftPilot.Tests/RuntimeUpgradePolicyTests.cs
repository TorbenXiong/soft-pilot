using SoftPilot.Application;
using SoftPilot.Domain;

namespace SoftPilot.Tests;

[TestClass]
public sealed class RuntimeUpgradePolicyTests
{
    [TestMethod]
    [DataRow(RuntimeKind.Node, "24.19.0", "24.18.0")]
    [DataRow(RuntimeKind.Java, "25.0.4+101.0.LTS", "25.0.4+7.0.LTS")]
    [DataRow(RuntimeKind.Python, "3.14.7", "3.14.6")]
    [DataRow(RuntimeKind.Redis, "8.2.10", "8.2.9")]
    [DataRow(RuntimeKind.MySql, "8.4.12", "8.4.11")]
    public void IsUpgradeAvailable_AcceptsNewerVersionInSameReleaseLine(
        RuntimeKind kind,
        string candidate,
        string installed)
    {
        Assert.IsTrue(RuntimeUpgradePolicy.IsUpgradeAvailable(kind, candidate, [installed]));
    }

    [TestMethod]
    public void IsUpgradeAvailable_RejectsDifferentNodeReleaseLine()
    {
        Assert.IsFalse(RuntimeUpgradePolicy.IsUpgradeAvailable(
            RuntimeKind.Node,
            "24.19.0",
            ["22.23.0"]));
    }

    [TestMethod]
    [DataRow("24.19.0", "24.19.0")]
    [DataRow("24.18.0", "24.19.0")]
    public void IsUpgradeAvailable_RejectsSameOrOlderCandidate(string candidate, string installed)
    {
        Assert.IsFalse(RuntimeUpgradePolicy.IsUpgradeAvailable(
            RuntimeKind.Node,
            candidate,
            [installed]));
    }
}
