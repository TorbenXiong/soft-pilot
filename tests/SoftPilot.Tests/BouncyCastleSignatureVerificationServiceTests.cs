using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using SoftPilot.Application;
using SoftPilot.Infrastructure.Security;

namespace SoftPilot.Tests;

[TestClass]
public sealed class BouncyCastleSignatureVerificationServiceTests
{
    private static readonly SigningMaterial Signing = CreateSigningMaterial();

    [TestMethod]
    public async Task VerifyDetachedSignatureAsync_WithTrustedValidSignature_Succeeds()
    {
        using var sandbox = new TemporaryDirectory();
        var (contentPath, signaturePath) = await WriteSignedContentAsync(sandbox.Path, "trusted content");
        var service = new BouncyCastleSignatureVerificationService();

        await service.VerifyDetachedSignatureAsync(
            contentPath,
            signaturePath,
            Signing.ArmoredPublicKey,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Signing.Fingerprint });
    }

    [TestMethod]
    public async Task VerifyDetachedSignatureAsync_WhenContentIsTampered_RejectsSignature()
    {
        using var sandbox = new TemporaryDirectory();
        var (contentPath, signaturePath) = await WriteSignedContentAsync(sandbox.Path, "original content");
        await File.WriteAllTextAsync(contentPath, "tampered content");
        var service = new BouncyCastleSignatureVerificationService();

        await Assert.ThrowsAsync<IntegrityException>(() => service.VerifyDetachedSignatureAsync(
            contentPath,
            signaturePath,
            Signing.ArmoredPublicKey,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Signing.Fingerprint }));
    }

    [TestMethod]
    public async Task VerifyDetachedSignatureAsync_WhenFingerprintIsNotTrusted_RejectsKey()
    {
        using var sandbox = new TemporaryDirectory();
        var (contentPath, signaturePath) = await WriteSignedContentAsync(sandbox.Path, "trusted content");
        var service = new BouncyCastleSignatureVerificationService();

        await Assert.ThrowsAsync<IntegrityException>(() => service.VerifyDetachedSignatureAsync(
            contentPath,
            signaturePath,
            Signing.ArmoredPublicKey,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { new('0', 40) }));
    }

    private static async Task<(string ContentPath, string SignaturePath)> WriteSignedContentAsync(
        string directory,
        string content)
    {
        var contentPath = Path.Combine(directory, "content.txt");
        var signaturePath = Path.Combine(directory, "content.txt.sig");
        var bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(contentPath, bytes);
        await File.WriteAllTextAsync(signaturePath, CreateArmoredSignature(bytes));
        return (contentPath, signaturePath);
    }

    private static SigningMaterial CreateSigningMaterial()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(
            Org.BouncyCastle.Math.BigInteger.ValueOf(0x10001),
            new SecureRandom(),
            2048,
            80));
        AsymmetricCipherKeyPair keyPair = generator.GenerateKeyPair();
        var pgpKeyPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyPair, DateTime.UtcNow);

        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        {
            pgpKeyPair.PublicKey.Encode(armored);
        }

        return new SigningMaterial(
            pgpKeyPair,
            Encoding.ASCII.GetString(output.ToArray()),
            Convert.ToHexString(pgpKeyPair.PublicKey.GetFingerprint()));
    }

    private static string CreateArmoredSignature(byte[] content)
    {
        var generator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        generator.InitSign(PgpSignature.BinaryDocument, Signing.KeyPair.PrivateKey);
        generator.Update(content, 0, content.Length);

        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        {
            generator.Generate().Encode(armored);
        }

        return Encoding.ASCII.GetString(output.ToArray());
    }

    private sealed record SigningMaterial(
        PgpKeyPair KeyPair,
        string ArmoredPublicKey,
        string Fingerprint);
}
