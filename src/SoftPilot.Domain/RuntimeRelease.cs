namespace SoftPilot.Domain;

public sealed record RuntimeRelease(
    RuntimeKind Kind,
    string Version,
    RuntimeArchitecture Architecture,
    Uri DownloadUri,
    string? Sha256,
    Uri? ChecksumUri = null,
    Uri? SignatureUri = null,
    bool IsLongTermSupport = false);
