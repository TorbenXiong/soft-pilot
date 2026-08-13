using System.Globalization;
using System.Text.RegularExpressions;

namespace SoftPilot.Infrastructure.Providers;

public sealed class RuntimeVersionComparer : IComparer<string>
{
    public static RuntimeVersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var left = ReadNumbers(x);
        var right = ReadNumbers(y);
        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var leftPart = index < left.Count ? left[index] : 0;
            var rightPart = index < right.Count ? right[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    private static IReadOnlyList<long> ReadNumbers(string version) => Regex.Matches(version, "\\d+")
        .Select(match => long.Parse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture))
        .ToArray();
}
