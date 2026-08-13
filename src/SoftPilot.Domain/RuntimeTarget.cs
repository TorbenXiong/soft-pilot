namespace SoftPilot.Domain;

public readonly record struct RuntimeTarget(RuntimeKind Kind, string Version)
{
    public static bool TryParse(string value, out RuntimeTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf('@');
        if (separator <= 0 || separator == value.Length - 1 || value.LastIndexOf('@') != separator)
        {
            return false;
        }

        if (!Enum.TryParse<RuntimeKind>(value[..separator], true, out var kind))
        {
            return false;
        }

        var version = value[(separator + 1)..].Trim();
        if (version.Length == 0 || version.Any(char.IsWhiteSpace))
        {
            return false;
        }

        target = new RuntimeTarget(kind, version);
        return true;
    }

    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}@{Version}";
}
