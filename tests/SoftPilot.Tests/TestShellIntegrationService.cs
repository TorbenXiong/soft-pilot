using SoftPilot.Application.Abstractions;

namespace SoftPilot.Tests;

internal sealed class TestShellIntegrationService : IShellIntegrationService
{
    public bool IsEnabled { get; set; }
    public int EnableCalls { get; private set; }
    public int DisableCalls { get; private set; }
    public Exception? EnableFailure { get; set; }
    public Exception? DisableFailure { get; set; }

    public Task<ShellIntegrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ShellIntegrationStatus(IsEnabled, IsEnabled, null));
    }

    public Task EnableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnableCalls++;
        if (EnableFailure is not null)
        {
            throw EnableFailure;
        }

        IsEnabled = true;
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisableCalls++;
        if (DisableFailure is not null)
        {
            throw DisableFailure;
        }

        IsEnabled = false;
        return Task.CompletedTask;
    }
}
