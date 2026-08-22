using System.Globalization;
using System.Text.RegularExpressions;

namespace SoftPilot.Application;

public static class RuntimeUpgradePolicy
{
    public static bool IsUpgradeAvailable(
        RuntimeKind kind,
        string candidateVersion,
        IEnumerable<string> installedVersions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateVersion);
        ArgumentNullException.ThrowIfNull(installedVersions);
        var candidateLine = GetReleaseLine(kind, candidateVersion);
        return installedVersions.Any(installedVersion =>
            string.Equals(candidateLine, GetReleaseLine(kind, installedVersion), StringComparison.Ordinal)
            && CompareVersions(candidateVersion, installedVersion) > 0);
    }

    public static bool IsSameReleaseLine(RuntimeKind kind, string leftVersion, string rightVersion) =>
        string.Equals(
            GetReleaseLine(kind, leftVersion),
            GetReleaseLine(kind, rightVersion),
            StringComparison.Ordinal);

    public static string GetReleaseLine(RuntimeKind kind, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var partCount = kind is RuntimeKind.Python or RuntimeKind.MySql ? 2 : 1;
        var parts = Regex.Matches(version, "\\d+")
            .Take(partCount)
            .Select(match => match.Value)
            .ToArray();
        return parts.Length == partCount ? string.Join('.', parts) : version;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = ReadNumbers(left);
        var rightParts = ReadNumbers(right);
        for (var index = 0; index < Math.Max(leftParts.Count, rightParts.Count); index++)
        {
            var leftPart = index < leftParts.Count ? leftParts[index] : 0;
            var rightPart = index < rightParts.Count ? rightParts[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static IReadOnlyList<long> ReadNumbers(string version) => Regex.Matches(version, "\\d+")
        .Select(match => long.Parse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture))
        .ToArray();
}
