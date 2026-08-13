using SoftPilot.Application;
using SoftPilot.Domain;

namespace SoftPilot.Tests;

[TestClass]
public sealed class NodeRuntimeTargetResolverTests
{
    [TestMethod]
    public void ResolveForInstall_SupportsLtsLatestAndVersionLines()
    {
        var available = new[]
        {
            Release("22.22.0", true),
            Release("24.18.1", true),
            Release("24.19.0", true),
            Release("26.1.0", false),
        };

        Assert.AreEqual("24.19.0", NodeRuntimeTargetResolver.ResolveForInstall(Target("lts"), available).Version);
        Assert.AreEqual("26.1.0", NodeRuntimeTargetResolver.ResolveForInstall(Target("latest"), available).Version);
        Assert.AreEqual("24.19.0", NodeRuntimeTargetResolver.ResolveForInstall(Target("24"), available).Version);
        Assert.AreEqual("24.19.0", NodeRuntimeTargetResolver.ResolveForInstall(Target("24.19"), available).Version);
    }

    [TestMethod]
    public void ResolveForUse_SelectsOnlyInstalledMatchingVersions()
    {
        var installed = new[]
        {
            Installation("22.23.2"),
            Installation("24.18.0"),
            Installation("24.19.0"),
        };

        Assert.AreEqual(
            "24.19.0",
            NodeRuntimeTargetResolver.ResolveForUse(Target("latest-installed"), installed).Version);
        Assert.AreEqual(
            "22.23.2",
            NodeRuntimeTargetResolver.ResolveForUse(Target("22"), installed).Version);
    }

    [TestMethod]
    public void ResolveForUse_LtsIntersectsInstalledVersionsWithOfficialCatalog()
    {
        var installed = new[]
        {
            Installation("24.19.0"),
            Installation("26.1.0"),
        };
        var available = new[]
        {
            Release("24.19.0", true),
            Release("26.1.0", false),
        };

        Assert.AreEqual(
            "24.19.0",
            NodeRuntimeTargetResolver.ResolveForUse(Target("lts"), installed, available).Version);
    }

    [TestMethod]
    public void ExactVersion_RemovesOptionalVPrefix()
    {
        Assert.AreEqual(
            "24.19.0",
            NodeRuntimeTargetResolver.ResolveForInstall(Target("v24.19.0"), []).Version);
    }

    private static RuntimeTarget Target(string version) => new(RuntimeKind.Node, version);

    private static RuntimeRelease Release(string version, bool isLts) => new(
        RuntimeKind.Node,
        version,
        RuntimeArchitecture.X64,
        new Uri($"https://nodejs.org/dist/v{version}/node-v{version}-win-x64.zip"),
        null,
        IsLongTermSupport: isLts);

    private static RuntimeInstallation Installation(string version) => new(
        RuntimeKind.Node,
        version,
        RuntimeArchitecture.X64,
        $@"D:\SoftPilot\app\node\{version}",
        DateTimeOffset.UtcNow,
        false);
}
