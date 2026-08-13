namespace SoftPilot.Application.Abstractions;

public interface IRootRegistry
{
    string? ReadRoot();
    void WriteRoot(string root);
    void DeleteRoot();
}
