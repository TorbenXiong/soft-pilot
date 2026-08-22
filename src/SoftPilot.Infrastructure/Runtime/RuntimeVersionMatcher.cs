namespace SoftPilot.Infrastructure.Runtime;

internal static class RuntimeVersionMatcher
{
    public static bool AreEquivalent(RuntimeKind kind, string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        static string Normalize(RuntimeKind runtimeKind, string value)
        {
            var normalized = value.Trim().Trim('"').TrimStart('v');
            if (normalized.StartsWith("1.8.0_", StringComparison.OrdinalIgnoreCase))
            {
                normalized = $"8.0.{ReadLeadingDigits(normalized[6..])}";
            }

            if (runtimeKind == RuntimeKind.Java)
            {
                return RuntimeVersionDisplayFormatter.Format(runtimeKind, normalized);
            }

            var buildSeparator = normalized.IndexOfAny(['+', '-']);
            return buildSeparator < 0 ? normalized : normalized[..buildSeparator];
        }

        static string ReadLeadingDigits(string value)
        {
            var length = 0;
            while (length < value.Length && char.IsDigit(value[length]))
            {
                length++;
            }

            return value[..length];
        }

        return string.Equals(
            Normalize(kind, expected),
            Normalize(kind, actual),
            StringComparison.OrdinalIgnoreCase);
    }
}
