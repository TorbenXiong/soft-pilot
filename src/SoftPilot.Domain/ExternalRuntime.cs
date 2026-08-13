namespace SoftPilot.Domain;

public sealed record ExternalRuntime(
    RuntimeKind Kind,
    string Version,
    RuntimeArchitecture Architecture,
    string ExecutablePath,
    string Source);
