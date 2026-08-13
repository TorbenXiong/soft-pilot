using System.Globalization;
using System.Text.RegularExpressions;

namespace SoftPilot.Application;

public static class RecommendedRuntimeReleaseSelector
{
    private const int NodeReleaseLineLimit = 2;
    private const int PythonReleaseLineLimit = 5;
    private static readonly Regex StablePythonVersionPattern = new(
        "^\\d+\\.\\d+\\.\\d+$",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<RuntimeRelease> Select(
        RuntimeKind kind,
        IEnumerable<RuntimeRelease> releases)
    {
        var candidates = releases
            .Where(release => release.Kind == kind && release.Architecture == RuntimeArchitecture.X64);

        return kind switch
        {
            RuntimeKind.Node => SelectLatestPerLine(candidates.Where(release => release.IsLongTermSupport), 1)
                .Take(NodeReleaseLineLimit)
                .ToArray(),
            RuntimeKind.Java => SelectLatestPerLine(candidates.Where(release => release.IsLongTermSupport), 1),
            RuntimeKind.Python => SelectLatestPerLine(
                    candidates.Where(release => StablePythonVersionPattern.IsMatch(release.Version)),
                    2)
                .Take(PythonReleaseLineLimit)
                .ToArray(),
            _ => [],
        };
    }

    private static IReadOnlyList<RuntimeRelease> SelectLatestPerLine(
        IEnumerable<RuntimeRelease> releases,
        int linePartCount)
    {
        return releases
            .Select(release => new { Release = release, Line = GetReleaseLine(release.Version, linePartCount) })
            .Where(item => item.Line is not null)
            .GroupBy(item => item.Line!, StringComparer.Ordinal)
            .Select(group => group
                .Select(item => item.Release)
                .OrderByDescending(release => release.Version, NumericVersionComparer.Instance)
                .First())
            .OrderByDescending(release => release.Version, NumericVersionComparer.Instance)
            .ToArray();
    }

    private static string? GetReleaseLine(string version, int partCount)
    {
        var parts = Regex.Matches(version, "\\d+")
            .Take(partCount)
            .Select(match => match.Value)
            .ToArray();
        return parts.Length == partCount ? string.Join('.', parts) : null;
    }

    private sealed class NumericVersionComparer : IComparer<string>
    {
        public static NumericVersionComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

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
}
