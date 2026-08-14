using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;

namespace SoftPilot.Tests;

internal sealed class TestRuntimeProvider : IRuntimeProvider
{
    private readonly Func<string, CancellationToken, Task> _prepare;
    private readonly Func<string, CancellationToken, Task<RuntimeHealth>> _checkHealth;

    public TestRuntimeProvider(
        RuntimeKind kind,
        string version,
        Func<string, CancellationToken, Task>? prepare = null,
        Func<string, CancellationToken, Task<RuntimeHealth>>? checkHealth = null)
    {
        Kind = kind;
        Version = version;
        _prepare = prepare ?? ((directory, _) =>
        {
            Directory.CreateDirectory(directory);
            return Task.CompletedTask;
        });
        _checkHealth = checkHealth ?? ((_, _) => Task.FromResult(new RuntimeHealth(true, version)));
    }

    public RuntimeKind Kind { get; }

    public string Version { get; }

    public int AvailableCallCount { get; private set; }

    public Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        AvailableCallCount++;
        return Task.FromResult<IReadOnlyList<RuntimeRelease>>([CreateRelease()]);
    }

    public Task<RuntimeRelease> ResolveAsync(string exactVersion, CancellationToken cancellationToken = default) =>
        string.Equals(exactVersion, Version, StringComparison.Ordinal)
            ? Task.FromResult(CreateRelease())
            : throw new InvalidOperationException($"Unexpected version {exactVersion}.");

    public Task PrepareAsync(
        RuntimeRelease release,
        string stagingDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _prepare(stagingDirectory, cancellationToken);

    public Task<RuntimeHealth> CheckHealthAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken = default) =>
        _checkHealth(runtimeDirectory, cancellationToken);

    private RuntimeRelease CreateRelease() => new(
        Kind,
        Version,
        RuntimeArchitecture.X64,
        new Uri($"https://example.invalid/{Kind}/{Version}.zip"),
        null,
        ReleasePageUri: new Uri($"https://example.invalid/{Kind}/{Version}/"));
}
