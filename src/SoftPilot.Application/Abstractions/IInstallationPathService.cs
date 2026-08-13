namespace SoftPilot.Application.Abstractions;

public interface IInstallationPathService
{
    string ResolveRoot(string selectedParentDirectory);
    InstallationPathValidation Validate(string selectedParentDirectory);
}

public sealed record InstallationPathValidation(
    string SelectedParent,
    string FinalRoot,
    bool IsValid,
    IReadOnlyList<string> Errors);
