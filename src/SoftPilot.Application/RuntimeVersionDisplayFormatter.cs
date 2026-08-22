using System.Text.RegularExpressions;

namespace SoftPilot.Application;

public static partial class RuntimeVersionDisplayFormatter
{
    public static string Format(RuntimeKind kind, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (kind != RuntimeKind.Java)
        {
            return version;
        }

        var match = TemurinSemverPattern().Match(version);
        if (!match.Success)
        {
            return version;
        }

        var displayVersion = match.Groups["version"].Value;
        if (version.EndsWith(".LTS", StringComparison.OrdinalIgnoreCase)
            && displayVersion.Count(character => character == '.') == 2
            && long.TryParse(
                match.Groups["build"].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var encodedBuild)
            && encodedBuild >= 100)
        {
            return $"{displayVersion}.{encodedBuild / 100}";
        }

        return displayVersion;
    }

    [GeneratedRegex(
        "^(?<version>\\d+(?:\\.\\d+){1,3})\\+(?<build>\\d+)(?:\\.\\d+)?(?:\\.LTS)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TemurinSemverPattern();
}
