namespace SoftPilot.Application.Abstractions;

public interface IEnvironmentVariableService
{
    Task<IReadOnlyList<EnvironmentVariableEntry>> GetAllAsync(
        EnvironmentVariableScope scope,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string name,
        string value,
        EnvironmentVariableScope scope,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string name,
        EnvironmentVariableScope scope,
        CancellationToken cancellationToken = default);
}
