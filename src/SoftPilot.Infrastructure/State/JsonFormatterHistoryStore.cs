using System.Text.Json;

namespace SoftPilot.Infrastructure.State;

public sealed class JsonFormatterHistoryStore : IJsonFormatterHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _historyPath;

    public JsonFormatterHistoryStore(IInstallationLayout layout)
    {
        _historyPath = Path.Combine(layout.DataDirectory, "toolbox", "json-history.json");
    }

    public async Task<IReadOnlyList<JsonFormatterHistoryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                _historyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<JsonFormatterHistoryEntry>>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? throw new SoftPilotException("JSON 历史记录为空。");
        }
        catch (JsonException exception)
        {
            throw new SoftPilotException("JSON 历史记录已损坏。", exception);
        }
        catch (IOException exception)
        {
            throw new SoftPilotException("无法读取 JSON 历史记录。", exception);
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<JsonFormatterHistoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_historyPath)
            ?? throw new SoftPilotException("无法确定 JSON 历史记录目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = _historyPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        catch (IOException exception)
        {
            throw new SoftPilotException("无法保存 JSON 历史记录。", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
