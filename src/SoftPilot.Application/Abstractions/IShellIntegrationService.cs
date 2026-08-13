namespace SoftPilot.Application.Abstractions;

public interface IShellIntegrationService
{
    Task<ShellIntegrationStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task EnableAsync(CancellationToken cancellationToken = default);
    Task DisableAsync(CancellationToken cancellationToken = default);
}

public sealed record ShellIntegrationStatus(
    bool IsEnabled,
    bool IsShimPathFirst,
    string? JavaHome,
    string? Problem = null);
