namespace SoftPilot.Infrastructure.Providers;

internal static class RuntimeDownloadSources
{
    private static readonly Uri NodeOfficialBaseUri = new("https://nodejs.org/dist/");
    private static readonly Uri NodeTunaBaseUri = new("https://mirrors.tuna.tsinghua.edu.cn/nodejs-release/");
    private static readonly Uri TemurinTunaBaseUri = new("https://mirrors.tuna.tsinghua.edu.cn/Adoptium/");

    public static IReadOnlyList<DownloadSourceCandidate> ForNode(Uri officialUri)
    {
        var sources = new List<DownloadSourceCandidate>
        {
            new("Node.js 官方源", officialUri),
        };
        if (IsBelowBaseUri(officialUri, NodeOfficialBaseUri))
        {
            var relativePath = NodeOfficialBaseUri.MakeRelativeUri(officialUri);
            sources.Add(new DownloadSourceCandidate(
                "清华 TUNA 镜像",
                new Uri(NodeTunaBaseUri, relativePath)));
        }

        return sources;
    }

    public static IReadOnlyList<DownloadSourceCandidate> ForTemurin(
        string version,
        Uri officialUri,
        string artifactFileName)
    {
        var sources = new List<DownloadSourceCandidate>
        {
            new("Eclipse Temurin 官方源", officialUri),
        };
        var feature = ReadFeatureVersion(version);
        var fileName = Path.GetFileName(artifactFileName);
        if (feature is not null
            && !string.IsNullOrWhiteSpace(fileName)
            && string.Equals(fileName, artifactFileName, StringComparison.Ordinal))
        {
            var relativePath = $"{feature}/jdk/x64/windows/{Uri.EscapeDataString(fileName)}";
            sources.Add(new DownloadSourceCandidate(
                "清华 TUNA 镜像",
                new Uri(TemurinTunaBaseUri, relativePath)));
        }

        return sources;
    }

    private static bool IsBelowBaseUri(Uri candidate, Uri baseUri) =>
        string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == baseUri.Port
        && candidate.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal);

    private static string? ReadFeatureVersion(string version)
    {
        var digits = version.TakeWhile(char.IsAsciiDigit).ToArray();
        return digits.Length == 0 ? null : new string(digits);
    }
}
