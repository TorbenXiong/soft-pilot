using SoftPilot.Application;
using SoftPilot.Domain;
using SoftPilot.Infrastructure.Tools;

namespace SoftPilot.Tests;

[TestClass]
public sealed class GitServiceTests
{
    [TestMethod]
    public void InstallationLayout_PlacesGitInAppDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new SoftPilot.Infrastructure.Installation.WindowsInstallationLayout(temporary.Path);

        Assert.AreEqual(Path.Combine(layout.AppDirectory, "git"), layout.GitDirectory);
    }

    [TestMethod]
    public void ParseLatestRelease_AcceptsOfficialPortableX64AssetWithSha256()
    {
        const string json = """
            {
              "tag_name": "v2.55.0.windows.4",
              "html_url": "https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.4",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "PortableGit-2.55.0.4-64-bit.7z.exe",
                  "browser_download_url": "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.4/PortableGit-2.55.0.4-64-bit.7z.exe",
                  "digest": "sha256:016e84230a3767f0c6b3788e79ba0c58a17377086801719d46700fca4f7b36b5"
                }
              ]
            }
            """;

        var release = GitService.ParseLatestRelease(json);

        Assert.AreEqual("2.55.0.windows.4", release.Version);
        Assert.AreEqual("PortableGit-2.55.0.4-64-bit.7z.exe", release.AssetName);
        Assert.AreEqual(
            "016e84230a3767f0c6b3788e79ba0c58a17377086801719d46700fca4f7b36b5",
            release.Sha256);
    }

    [TestMethod]
    public void ParseLatestRelease_RejectsUntrustedOrUnverifiedAssets()
    {
        const string untrustedJson = """
            {
              "tag_name": "v2.55.0.windows.4",
              "html_url": "https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.4",
              "draft": false,
              "prerelease": false,
              "assets": [{
                "name": "PortableGit-2.55.0.4-64-bit.7z.exe",
                "browser_download_url": "https://example.test/PortableGit-2.55.0.4-64-bit.7z.exe",
                "digest": "sha256:016e84230a3767f0c6b3788e79ba0c58a17377086801719d46700fca4f7b36b5"
              }]
            }
            """;
        const string missingDigestJson = """
            {
              "tag_name": "v2.55.0.windows.4",
              "html_url": "https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.4",
              "draft": false,
              "prerelease": false,
              "assets": [{
                "name": "PortableGit-2.55.0.4-64-bit.7z.exe",
                "browser_download_url": "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.4/PortableGit-2.55.0.4-64-bit.7z.exe"
              }]
            }
            """;

        Assert.ThrowsExactly<IntegrityException>(() => GitService.ParseLatestRelease(untrustedJson));
        Assert.ThrowsExactly<IntegrityException>(() => GitService.ParseLatestRelease(missingDigestJson));
    }

    [TestMethod]
    [DataRow("git version 2.55.0.windows.4", "2.55.0.windows.4")]
    [DataRow("git version 2.55.0.4", "2.55.0.4")]
    public void ParseInstalledVersion_ReadsGitForWindowsVersion(string output, string expected)
    {
        Assert.AreEqual(expected, GitService.ParseInstalledVersion(output));
    }

    [TestMethod]
    public void GetInstallOperationName_DistinguishesInstallAndUpgrade()
    {
        Assert.AreEqual("install", GitService.GetInstallOperationName(isInstalled: false));
        Assert.AreEqual("upgrade", GitService.GetInstallOperationName(isInstalled: true));
    }

    [TestMethod]
    [DataRow(RuntimeKind.Java, "21.0.12+8.0.LTS", "21.0.12")]
    [DataRow(RuntimeKind.Java, "17.0.20+8", "17.0.20")]
    [DataRow(RuntimeKind.Node, "24.19.0", "24.19.0")]
    public void VersionDisplayFormatter_UsesConciseJavaBuild(RuntimeKind kind, string version, string expected)
    {
        Assert.AreEqual(expected, RuntimeVersionDisplayFormatter.Format(kind, version));
    }
}
