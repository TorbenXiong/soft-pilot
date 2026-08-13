namespace SoftPilot.Infrastructure.Runtime;

internal static class RuntimeVersionMatcher
{
    public static bool AreEquivalent(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        static string Normalize(string value) => value.Trim().TrimStart('v').Replace('+', '_');
        var normalizedExpected = Normalize(expected);
        var normalizedActual = Normalize(actual);
        return string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase)
            || normalizedExpected.StartsWith(normalizedActual, StringComparison.OrdinalIgnoreCase)
            || normalizedActual.StartsWith(normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }
}
