namespace SoftPilot.Application.Abstractions;

public interface IJsonFormatterHistoryStore
{
    Task<IReadOnlyList<JsonFormatterHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyList<JsonFormatterHistoryEntry> entries,
        CancellationToken cancellationToken = default);
}
