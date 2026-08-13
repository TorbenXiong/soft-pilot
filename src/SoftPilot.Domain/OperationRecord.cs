namespace SoftPilot.Domain;

public enum OperationStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record OperationRecord(
    Guid Id,
    string Name,
    RuntimeKind? Kind,
    string? Version,
    OperationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    string? Error = null);
