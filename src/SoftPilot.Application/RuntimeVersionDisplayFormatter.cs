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
        return match.Success
            ? match.Groups["version"].Value
            : version;
    }

    [GeneratedRegex(
        "^(?<version>\\d+(?:\\.\\d+){1,3})\\+(?<build>\\d+)(?:\\.\\d+)?(?:\\.LTS)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TemurinSemverPattern();
}
