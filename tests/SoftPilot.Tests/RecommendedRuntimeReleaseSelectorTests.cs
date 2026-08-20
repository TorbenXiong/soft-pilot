using SoftPilot.Application;
using SoftPilot.Domain;

namespace SoftPilot.Tests;

[TestClass]
public sealed class RecommendedRuntimeReleaseSelectorTests
{
    [TestMethod]
    public void Select_Node_ReturnsLatestPatchForTwoNewestLtsLines()
    {
        var releases = new[]
        {
            Release(RuntimeKind.Node, "22.22.0", isLts: true),
            Release(RuntimeKind.Node, "24.18.0", isLts: true),
            Release(RuntimeKind.Node, "26.7.0", isLts: false),
            Release(RuntimeKind.Node, "20.20.2", isLts: true),
            Release(RuntimeKind.Node, "24.19.0", isLts: true),
            Release(RuntimeKind.Node, "22.23.2", isLts: true),
        };

        var selected = RecommendedRuntimeReleaseSelector.Select(RuntimeKind.Node, releases);

        CollectionAssert.AreEqual(
            new[] { "24.19.0", "22.23.2" },
            selected.Select(release => release.Version).ToArray());
    }

    [TestMethod]
    public void Select_Java_ReturnsLatestGaForEveryLtsLine()
    {
        var releases = new[]
        {
            Release(RuntimeKind.Java, "17.0.19+10", isLts: true),
            Release(RuntimeKind.Java, "21.0.12+8.0.LTS", isLts: true),
            Release(RuntimeKind.Java, "17.0.20+8", isLts: true),
            Release(RuntimeKind.Java, "25.0.4+7.0.LTS", isLts: true),
            Release(RuntimeKind.Java, "24.0.2+12", isLts: false),
            Release(RuntimeKind.Java, "11.0.32+9", isLts: true),
            Release(RuntimeKind.Java, "8.0.502+7", isLts: true),
        };

        var selected = RecommendedRuntimeReleaseSelector.Select(RuntimeKind.Java, releases);

        CollectionAssert.AreEqual(
            new[] { "25.0.4+7.0.LTS", "21.0.12+8.0.LTS", "17.0.20+8", "11.0.32+9", "8.0.502+7" },
            selected.Select(release => release.Version).ToArray());
    }

    [TestMethod]
    public void Select_Python_ReturnsLatestPatchForFiveNewestStableLines()
    {
        var releases = new[]
        {
            Release(RuntimeKind.Python, "3.12.9"),
            Release(RuntimeKind.Python, "3.14.7"),
            Release(RuntimeKind.Python, "3.15.0rc1"),
            Release(RuntimeKind.Python, "3.10.11"),
            Release(RuntimeKind.Python, "3.13.15"),
            Release(RuntimeKind.Python, "3.9.13"),
            Release(RuntimeKind.Python, "3.11.9"),
            Release(RuntimeKind.Python, "3.12.10"),
        };

        var selected = RecommendedRuntimeReleaseSelector.Select(RuntimeKind.Python, releases);

        CollectionAssert.AreEqual(
            new[] { "3.14.7", "3.13.15", "3.12.10", "3.11.9", "3.10.11" },
            selected.Select(release => release.Version).ToArray());
    }

    [TestMethod]
    public void Select_Redis_ReturnsLatestPatchForEveryAvailableMajorLine()
    {
        var releases = new[]
        {
            Release(RuntimeKind.Redis, "8.10.0"),
            Release(RuntimeKind.Redis, "8.10.1"),
            Release(RuntimeKind.Redis, "8.8.2"),
            Release(RuntimeKind.Redis, "8.6.6"),
            Release(RuntimeKind.Redis, "8.4.6"),
            Release(RuntimeKind.Redis, "8.2.9"),
            Release(RuntimeKind.Redis, "8.0.6"),
            Release(RuntimeKind.Redis, "7.4.11"),
            Release(RuntimeKind.Redis, "7.2.16"),
            Release(RuntimeKind.Redis, "6.2.24"),
            Release(RuntimeKind.Redis, "5.0.14"),
        };

        var selected = RecommendedRuntimeReleaseSelector.Select(RuntimeKind.Redis, releases);

        CollectionAssert.AreEqual(
            new[] { "8.10.1", "7.4.11", "6.2.24", "5.0.14" },
            selected.Select(release => release.Version).ToArray());
    }

    [TestMethod]
    public void Select_MySql_ReturnsLatestPatchForEachMajorMinorLine()
    {
        var releases = new[]
        {
            Release(RuntimeKind.MySql, "8.4.10", isLts: true),
            Release(RuntimeKind.MySql, "8.4.11", isLts: true),
            Release(RuntimeKind.MySql, "5.7.43"),
            Release(RuntimeKind.MySql, "5.7.44"),
        };

        var selected = RecommendedRuntimeReleaseSelector.Select(RuntimeKind.MySql, releases);

        CollectionAssert.AreEqual(
            new[] { "8.4.11", "5.7.44" },
            selected.Select(release => release.Version).ToArray());
    }

    private static RuntimeRelease Release(RuntimeKind kind, string version, bool isLts = false) => new(
        kind,
        version,
        RuntimeArchitecture.X64,
        new Uri($"https://example.invalid/{kind}/{version}"),
        null,
        IsLongTermSupport: isLts);
}
