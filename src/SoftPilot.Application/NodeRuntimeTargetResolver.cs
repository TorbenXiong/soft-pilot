using System.Globalization;
using System.Text.RegularExpressions;

namespace SoftPilot.Application;

public static class NodeRuntimeTargetResolver
{
    private static readonly Regex ExactVersionPattern = new(
        "^v?\\d+\\.\\d+\\.\\d+(?:[-+].+)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex VersionLinePattern = new(
        "^\\d+(?:\\.\\d+)?$",
        RegexOptions.CultureInvariant);

    public static bool IsAlias(RuntimeTarget target) =>
        target.Kind == RuntimeKind.Node && !ExactVersionPattern.IsMatch(target.Version);

    public static RuntimeTarget ResolveForInstall(
        RuntimeTarget requested,
        IEnumerable<RuntimeRelease> available)
    {
        if (requested.Kind != RuntimeKind.Node || !IsAlias(requested))
        {
            return NormalizeExactVersion(requested);
        }

        var alias = requested.Version.ToLowerInvariant();
        var candidates = available.Where(release => release.Kind == RuntimeKind.Node);
        candidates = alias switch
        {
            "lts" => candidates.Where(release => release.IsLongTermSupport),
            "latest" or "node" => candidates,
            _ when VersionLinePattern.IsMatch(alias) => candidates.Where(release => IsInLine(release.Version, alias)),
            _ => throw new SoftPilotException(
                $"不支持的 Node.js 安装版本别名：{requested.Version}。可使用 lts、latest、主版本或精确版本。"),
        };

        var selected = SelectNewest(candidates)
            ?? throw new SoftPilotException($"官方目录中没有与 node@{requested.Version} 匹配的版本。");
        return new RuntimeTarget(RuntimeKind.Node, selected.Version);
    }

    public static RuntimeTarget ResolveForUse(
        RuntimeTarget requested,
        IEnumerable<RuntimeInstallation> installed,
        IEnumerable<RuntimeRelease>? available = null)
    {
        if (requested.Kind != RuntimeKind.Node || !IsAlias(requested))
        {
            return NormalizeExactVersion(requested);
        }

        var alias = requested.Version.ToLowerInvariant();
        var candidates = installed.Where(item => item.Kind == RuntimeKind.Node && !item.IsDeleted);
        candidates = alias switch
        {
            "latest-installed" or "newest" or "latest" or "node" => candidates,
            "lts" => FilterInstalledLts(candidates, available),
            _ when VersionLinePattern.IsMatch(alias) => candidates.Where(item => IsInLine(item.Version, alias)),
            _ => throw new SoftPilotException(
                $"不支持的 Node.js 切换版本别名：{requested.Version}。可使用 lts、latest-installed、主版本或精确版本。"),
        };

        var selected = SelectNewest(candidates)
            ?? throw new SoftPilotException($"没有已安装版本与 node@{requested.Version} 匹配。");
        return new RuntimeTarget(RuntimeKind.Node, selected.Version);
    }

    private static IEnumerable<RuntimeInstallation> FilterInstalledLts(
        IEnumerable<RuntimeInstallation> installed,
        IEnumerable<RuntimeRelease>? available)
    {
        if (available is null)
        {
            throw new SoftPilotException("解析 node@lts 需要读取 Node.js 官方版本目录。");
        }

        var ltsVersions = available
            .Where(release => release.Kind == RuntimeKind.Node && release.IsLongTermSupport)
            .Select(release => release.Version)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return installed.Where(item => ltsVersions.Contains(item.Version));
    }

    private static RuntimeTarget NormalizeExactVersion(RuntimeTarget target) =>
        target.Kind == RuntimeKind.Node && target.Version.StartsWith('v')
            ? target with { Version = target.Version[1..] }
            : target;

    private static bool IsInLine(string version, string line) =>
        version.StartsWith(line + '.', StringComparison.OrdinalIgnoreCase);

    private static RuntimeRelease? SelectNewest(IEnumerable<RuntimeRelease> releases) => releases
        .OrderByDescending(release => release.Version, NumericVersionComparer.Instance)
        .FirstOrDefault();

    private static RuntimeInstallation? SelectNewest(IEnumerable<RuntimeInstallation> installations) => installations
        .OrderByDescending(installation => installation.Version, NumericVersionComparer.Instance)
        .FirstOrDefault();

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
