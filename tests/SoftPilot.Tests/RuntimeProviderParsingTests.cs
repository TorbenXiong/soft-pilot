using SoftPilot.Domain;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Tests;

[TestClass]
public sealed class RuntimeProviderParsingTests
{
    [TestMethod]
    public void AdoptiumAvailableReleases_ReturnsDistinctLtsFeaturesNewestFirst()
    {
        const string json = """
            {
              "available_lts_releases": [8, 11, 17, 21, 25, 25],
              "most_recent_feature_release": 26,
              "most_recent_lts": 25
            }
            """;

        var features = TemurinRuntimeProvider.ParseAvailableLtsReleases(json);

        CollectionAssert.AreEqual(new[] { 25, 21, 17, 11, 8 }, features.ToArray());
    }

    [TestMethod]
    public void AdoptiumAvailableReleases_RejectsMissingEmptyOrInvalidLtsFeatures()
    {
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
            TemurinRuntimeProvider.ParseAvailableLtsReleases("{ \"available_releases\": [8, 11] }"));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
            TemurinRuntimeProvider.ParseAvailableLtsReleases("{ \"available_lts_releases\": [] }"));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
            TemurinRuntimeProvider.ParseAvailableLtsReleases("{ \"available_lts_releases\": [8, \"21\"] }"));
    }

    [TestMethod]
    public void NodeIndex_OnlyReturnsWindowsX64Zip_ReleasesNewestFirst()
    {
        const string json = """
            [
              { "version": "v22.10.0", "files": ["win-x64-zip"], "lts": "Jod" },
              { "version": "v23.1.0", "files": ["linux-x64"] },
              { "version": "v24.2.1", "files": ["win-x64-zip"], "lts": false }
            ]
            """;

        var releases = NodeRuntimeProvider.ParseReleases(json);

        CollectionAssert.AreEqual(new[] { "24.2.1", "22.10.0" }, releases.Select(item => item.Version).ToArray());
        Assert.IsFalse(releases[0].IsLongTermSupport);
        Assert.IsTrue(releases[1].IsLongTermSupport);
        Assert.AreEqual(RuntimeArchitecture.X64, releases[0].Architecture);
        Assert.AreEqual("https://nodejs.org/dist/v24.2.1/node-v24.2.1-win-x64.zip", releases[0].DownloadUri.AbsoluteUri);
    }

    [TestMethod]
    public void PythonManagerJson_UsesVersionsObject_AndFiltersToStablePythonCoreX64()
    {
        const string json = """
            {
              "versions": [
                { "company": "PythonCore", "tag": "3.13-64", "sort-version": "3.13.15" },
                { "company": "PythonCore", "tag": "3.14-64", "sort-version": "3.14.7" },
                { "company": "PythonCore", "tag": "3.15-dev-64", "sort-version": "3.15.0rc1" },
                { "company": "PythonCore", "tag": "3.14t-64", "sort-version": "3.14.7" },
                { "company": "OtherPython", "tag": "3.14-64", "sort-version": "3.14.7" },
                { "company": "PythonCore", "tag": "3.14-32", "sort-version": "3.14.7" }
              ]
            }
            """;

        var releases = PythonRuntimeProvider.ParseReleases(json);

        CollectionAssert.AreEqual(new[] { "3.14.7", "3.13.15" }, releases.Select(item => item.Version).ToArray());
        Assert.IsTrue(releases.All(item => item.Architecture == RuntimeArchitecture.X64));
        Assert.IsTrue(releases.All(item => item.DownloadUri.AbsoluteUri == "https://www.python.org/ftp/python/index-windows.json"));
    }

    [TestMethod]
    public void PythonManagerJson_RejectsMissingVersionsArray()
    {
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
            PythonRuntimeProvider.ParseReleases("{ \"items\": [] }"));
    }
}
