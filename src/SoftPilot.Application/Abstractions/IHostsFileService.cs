namespace SoftPilot.Application.Abstractions;

public interface IHostsFileService
{
    string HostsPath { get; }

    Task<string> ReadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string content, CancellationToken cancellationToken = default);
}
