namespace SoftPilot.Domain;

public sealed record RuntimeInstallation(
    RuntimeKind Kind,
    string Version,
    RuntimeArchitecture Architecture,
    string InstallPath,
    DateTimeOffset InstalledAt,
    bool IsCurrent,
    DateTimeOffset? DeletedAt = null,
    string? TrashPath = null)
{
    public bool IsDeleted => DeletedAt is not null;
}
