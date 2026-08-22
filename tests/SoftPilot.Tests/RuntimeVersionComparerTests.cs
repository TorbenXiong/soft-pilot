using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Tests;

[TestClass]
public sealed class RuntimeVersionComparerTests
{
    [TestMethod]
    public void Compare_OrdersJavaFeatureAndPatchNumerically()
    {
        string[] versions =
        [
            "8.0.502+7",
            "21.0.9+10.0.LTS",
            "21.0.12+8.0.LTS",
            "25.0.4+7.0.LTS",
        ];

        var sorted = versions.OrderByDescending(value => value, RuntimeVersionComparer.Instance).ToArray();

        CollectionAssert.AreEqual(
            new[] { "25.0.4+7.0.LTS", "21.0.12+8.0.LTS", "21.0.9+10.0.LTS", "8.0.502+7" },
            sorted);
    }

    [TestMethod]
    public void Compare_OrdersMultiDigitPatchesAndEncodedJavaUpdatesNumerically()
    {
        string[] versions =
        [
            "24.9.0",
            "24.10.0",
            "25.0.4+7.0.LTS",
            "25.0.4+101.0.LTS",
        ];

        var sorted = versions.OrderByDescending(
            value => value,
            RuntimeVersionComparer.Instance).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "25.0.4+101.0.LTS",
                "25.0.4+7.0.LTS",
                "24.10.0",
                "24.9.0",
            },
            sorted);
    }
}
