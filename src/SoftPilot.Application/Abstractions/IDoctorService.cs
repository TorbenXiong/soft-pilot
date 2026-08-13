namespace SoftPilot.Application.Abstractions;

public interface IDoctorService
{
    Task<IReadOnlyList<DoctorCheck>> RunAsync(CancellationToken cancellationToken = default);
}

public sealed record DoctorCheck(string Name, bool Passed, string Message);
