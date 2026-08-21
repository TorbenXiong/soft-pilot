namespace SoftPilot.Application;

public static class EnvironmentPathValue
{
    public static IReadOnlyList<string> Split(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 0
            ? []
            : value.Split(';', StringSplitOptions.None);
    }

    public static string Join(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var values = entries.ToArray();
        foreach (var value in values)
        {
            ValidateEntry(value);
        }

        return string.Join(';', values);
    }

    public static void ValidateEntry(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains(';'))
        {
            throw new SoftPilotException("单个 PATH 路径项不能包含分号。");
        }

        if (value.Contains('\0'))
        {
            throw new SoftPilotException("PATH 路径项不能包含空字符。");
        }
    }

    public static EnvironmentPathResolution Resolve(string value)
    {
        ValidateEntry(value);
        var expanded = Environment.ExpandEnvironmentVariables(value);
        var existenceCandidate = expanded.Trim();
        if (existenceCandidate.Length >= 2
            && existenceCandidate[0] == '"'
            && existenceCandidate[^1] == '"')
        {
            existenceCandidate = existenceCandidate[1..^1];
        }

        return new EnvironmentPathResolution(
            expanded,
            existenceCandidate.Length > 0 && Directory.Exists(existenceCandidate));
    }
}

public sealed record EnvironmentPathResolution(string ExpandedValue, bool Exists);
