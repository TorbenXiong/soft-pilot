namespace SoftPilot.Application;

public sealed record JsonFormatterHistoryEntry(
    Guid Id,
    string Title,
    string Input,
    JsonFormattingMode Mode,
    DateTimeOffset UpdatedAt);

public enum JsonFormattingMode
{
    Beautified,
    Minified,
}
