namespace SoftPilot.Application.Abstractions;

public interface IRuntimeModulePreferencesStore
{
    Task<RuntimeModulePreferences> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(RuntimeModulePreferences preferences, CancellationToken cancellationToken = default);
}
