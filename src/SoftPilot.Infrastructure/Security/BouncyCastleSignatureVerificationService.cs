using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Utilities.IO;

namespace SoftPilot.Infrastructure.Security;

public sealed class BouncyCastleSignatureVerificationService : ISignatureVerificationService
{
    public async Task VerifyDetachedSignatureAsync(
        string contentPath,
        string signaturePath,
        string armoredPublicKey,
        IReadOnlySet<string> allowedFingerprints,
        CancellationToken cancellationToken = default)
    {
        if (allowedFingerprints.Count == 0)
        {
            throw new IntegrityException("没有配置受信任的发布密钥指纹，已拒绝验证。");
        }

        await using var signatureInput = File.OpenRead(signaturePath);
        using var decodedSignatureInput = PgpUtilities.GetDecoderStream(signatureInput);
        var signatureFactory = new PgpObjectFactory(decodedSignatureInput);
        var signatureObject = signatureFactory.NextPgpObject();
        if (signatureObject is PgpCompressedData compressedData)
        {
            using var compressedStream = compressedData.GetDataStream();
            signatureObject = new PgpObjectFactory(compressedStream).NextPgpObject();
        }

        if (signatureObject is not PgpSignatureList signatures || signatures.Count == 0)
        {
            throw new IntegrityException("签名文件不包含有效的 OpenPGP 分离签名。");
        }

        var signature = signatures[0];
        var (publicKey, primaryFingerprint) = FindTrustedSigningKey(
            armoredPublicKey,
            signature.KeyId,
            allowedFingerprints);

        signature.InitVerify(publicKey);
        await using var content = File.OpenRead(contentPath);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            signature.Update(buffer, 0, read);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!signature.Verify())
        {
            throw new IntegrityException($"OpenPGP 签名验证失败（主密钥 {primaryFingerprint}）。");
        }
    }

    private static (PgpPublicKey SigningKey, string PrimaryFingerprint) FindTrustedSigningKey(
        string armoredPublicKey,
        long signingKeyId,
        IReadOnlySet<string> allowedFingerprints)
    {
        foreach (var block in EnumerateArmoredKeyBlocks(armoredPublicKey))
        {
            using var keyInput = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(block));
            using var decodedKeyInput = PgpUtilities.GetDecoderStream(keyInput);
            var bundle = new PgpPublicKeyRingBundle(decodedKeyInput);
            foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
            {
                var signingKey = ring.GetPublicKey(signingKeyId);
                if (signingKey is null)
                {
                    continue;
                }

                var primaryFingerprint = Convert.ToHexString(ring.GetPublicKey().GetFingerprint());
                var signingFingerprint = Convert.ToHexString(signingKey.GetFingerprint());
                if (allowedFingerprints.Contains(primaryFingerprint) || allowedFingerprints.Contains(signingFingerprint))
                {
                    return (signingKey, primaryFingerprint);
                }

                throw new IntegrityException(
                    $"签名密钥属于未授权主密钥 {primaryFingerprint}（签名子密钥 {signingFingerprint}）。");
            }
        }

        throw new IntegrityException($"签名使用了未知密钥 0x{signingKeyId:X16}。");
    }

    private static IEnumerable<string> EnumerateArmoredKeyBlocks(string value)
    {
        const string begin = "-----BEGIN PGP PUBLIC KEY BLOCK-----";
        const string end = "-----END PGP PUBLIC KEY BLOCK-----";
        var offset = 0;
        while (true)
        {
            var start = value.IndexOf(begin, offset, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var finish = value.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
            if (finish < 0)
            {
                throw new IntegrityException("OpenPGP 公钥块不完整。");
            }

            finish += end.Length;
            yield return value[start..finish];
            offset = finish;
        }
    }
}
