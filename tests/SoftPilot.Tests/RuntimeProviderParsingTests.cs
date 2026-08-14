using SoftPilot.Domain;
using SoftPilot.Application;
using SoftPilot.Infrastructure.IO;
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
        Assert.AreEqual("https://nodejs.org/dist/v24.2.1/", releases[0].ReleasePageUri?.AbsoluteUri);
    }

    [TestMethod]
    public void PythonManagerJson_UsesVersionsObject_AndFiltersToStablePythonCoreX64()
    {
        const string json = """
            {
              "versions": [
                { "company": "PythonCore", "tag": "3.13-64", "sort-version": "3.13.15", "url": "https://www.python.org/ftp/python/3.13.15/python-3.13.15-amd64.zip", "hash": { "sha256": "6479223746cdfb79d25865110d6f524ac98de081324e119af1dc3ae36bddc7a5" } },
                { "company": "PythonCore", "tag": "3.14-64", "sort-version": "3.14.7", "url": "https://www.python.org/ftp/python/3.14.7/python-3.14.7-amd64.zip", "hash": { "sha256": "7479223746cdfb79d25865110d6f524ac98de081324e119af1dc3ae36bddc7a5" } },
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
        Assert.AreEqual(
            "https://www.python.org/ftp/python/3.14.7/python-3.14.7-amd64.zip",
            releases[0].DownloadUri.AbsoluteUri);
        Assert.AreEqual(
            "7479223746cdfb79d25865110d6f524ac98de081324e119af1dc3ae36bddc7a5",
            releases[0].Sha256);
        Assert.AreEqual(
            "https://www.python.org/downloads/release/python-3147/",
            releases[0].ReleasePageUri?.AbsoluteUri);
    }

    [TestMethod]
    public void TemurinDownloadUrl_BuildsVersionSpecificReleasePage()
    {
        var page = TemurinRuntimeProvider.CreateReleasePageUri(new Uri(
            "https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4%2B7/OpenJDK25U-jdk_x64_windows_hotspot_25.0.4_7.zip"));

        Assert.AreEqual(
            "https://github.com/adoptium/temurin25-binaries/releases#release-jdk-25.0.4+7",
            page?.AbsoluteUri);
    }

    [TestMethod]
    public void PythonManagerJson_RejectsMissingVersionsArray()
    {
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
            PythonRuntimeProvider.ParseReleases("{ \"items\": [] }"));
    }

    [TestMethod]
    public async Task PythonCatalog_RefreshesFromOfficialIndexWithoutInstallManager()
    {
        const string json = """
            {
              "versions": [
                { "company": "PythonCore", "tag": "3.14-64", "sort-version": "3.14.7", "url": "https://www.python.org/ftp/python/3.14.7/python-3.14.7-amd64.zip", "hash": { "sha256": "7479223746cdfb79d25865110d6f524ac98de081324e119af1dc3ae36bddc7a5" } }
              ],
              "next": null
            }
            """;
        var handler = new StaticJsonHandler(json);
        using var client = new HttpClient(handler);
        var provider = new PythonRuntimeProvider(client, new ProcessRunner());

        var releases = await provider.GetAvailableAsync();

        CollectionAssert.AreEqual(new[] { "3.14.7" }, releases.Select(item => item.Version).ToArray());
        Assert.AreEqual(
            "https://www.python.org/ftp/python/index-windows.json",
            handler.RequestUri?.AbsoluteUri);
    }

    [TestMethod]
    public void PythonCatalog_RejectsPaginationOutsideOfficialDirectory()
    {
        const string json = """
            {
              "versions": [],
              "next": "https://example.test/python-index.json"
            }
            """;

        Assert.ThrowsExactly<IntegrityException>(() =>
            PythonRuntimeProvider.ParseNextIndexUri(
                json,
                new Uri("https://www.python.org/ftp/python/index-windows.json")));
    }

    [TestMethod]
    public void PythonManagerJson_RejectsDownloadOutsideOfficialDirectory()
    {
        const string json = """
            {
              "versions": [
                { "company": "PythonCore", "tag": "3.14-64", "sort-version": "3.14.7", "url": "https://example.test/python.zip", "hash": { "sha256": "7479223746cdfb79d25865110d6f524ac98de081324e119af1dc3ae36bddc7a5" } }
              ]
            }
            """;

        Assert.ThrowsExactly<IntegrityException>(() => PythonRuntimeProvider.ParseReleases(json));
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json),
            });
        }
    }
}
