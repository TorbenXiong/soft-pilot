namespace SoftPilot.Application.Abstractions;

public interface ISignatureVerificationService
{
    Task VerifyDetachedSignatureAsync(
        string contentPath,
        string signaturePath,
        string armoredPublicKey,
        IReadOnlySet<string> allowedFingerprints,
        CancellationToken cancellationToken = default);
}
