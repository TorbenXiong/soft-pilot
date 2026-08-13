using System.Text.Json;

namespace SoftPilot.Infrastructure.State;

public sealed class JsonRuntimeModulePreferencesStore : IRuntimeModulePreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _preferencesPath;

    public JsonRuntimeModulePreferencesStore(IInstallationLayout layout)
    {
        _preferencesPath = Path.Combine(layout.DataDirectory, "ui-preferences.json");
    }

    public async Task<RuntimeModulePreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_preferencesPath))
        {
            return RuntimeModulePreferences.Default;
        }

        try
        {
            await using var stream = new FileStream(
                _preferencesPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<RuntimeModulePreferences>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? throw new SoftPilotException("界面模块配置为空，将使用默认配置。");
        }
        catch (JsonException exception)
        {
            throw new SoftPilotException("界面模块配置已损坏，将使用默认配置。", exception);
        }
        catch (IOException exception)
        {
            throw new SoftPilotException("无法读取界面模块配置。", exception);
        }
    }

    public async Task SaveAsync(
        RuntimeModulePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_preferencesPath)
            ?? throw new SoftPilotException("无法确定界面模块配置目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = _preferencesPath + $".{Guid.NewGuid():N}.tmp";
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
                await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _preferencesPath, overwrite: true);
        }
        catch (IOException exception)
        {
            throw new SoftPilotException("无法保存界面模块配置。", exception);
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
