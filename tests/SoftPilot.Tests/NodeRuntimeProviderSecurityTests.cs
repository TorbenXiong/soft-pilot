using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using SoftPilot.Application;
using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Tests;

[TestClass]
public sealed class NodeRuntimeProviderSecurityTests
{
    [TestMethod]
    public async Task PrepareAsync_VerifiesSignedManifestBeforeDownloadingAndExtractingArchive()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var signature = new RecordingSignatureVerifier();
        var downloads = new RecordingNodeDownloadService(signature);
        using var client = new HttpClient(new PublicKeyHandler());
        var provider = new NodeRuntimeProvider(
            client,
            downloads,
            signature,
            layout,
            new ProcessRunner());
        var release = CreateRelease();
        var staging = Path.Combine(layout.StagingDirectory, "node-test");

        await provider.PrepareAsync(release, staging);

        Assert.AreEqual(1, signature.CallCount);
        Assert.IsTrue(signature.AllowedFingerprintsCount > 0);
        Assert.AreEqual(1, downloads.ArchiveDownloadCount);
        Assert.AreEqual(RecordingNodeDownloadService.ExpectedHash, downloads.ArchiveExpectedHash);
        Assert.IsTrue(File.Exists(Path.Combine(staging, "node.exe")));
    }

    [TestMethod]
    public async Task PrepareAsync_WhenManifestSignatureFails_DoesNotDownloadArchive()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        layout.EnsureWorkspace();
        var signature = new RecordingSignatureVerifier
        {
            Failure = new IntegrityException("simulated invalid signature"),
        };
        var downloads = new RecordingNodeDownloadService(signature);
        using var client = new HttpClient(new PublicKeyHandler());
        var provider = new NodeRuntimeProvider(
            client,
            downloads,
            signature,
            layout,
            new ProcessRunner());

        await Assert.ThrowsAsync<IntegrityException>(() => provider.PrepareAsync(
            CreateRelease(),
            Path.Combine(layout.StagingDirectory, "node-test")));

        Assert.AreEqual(0, downloads.ArchiveDownloadCount);
    }

    private static RuntimeRelease CreateRelease()
    {
        const string version = "1.2.3";
        var directory = new Uri($"https://nodejs.org/dist/v{version}/");
        return new RuntimeRelease(
            RuntimeKind.Node,
            version,
            RuntimeArchitecture.X64,
            new Uri(directory, $"node-v{version}-win-x64.zip"),
            null,
            new Uri(directory, "SHASUMS256.txt"),
            new Uri(directory, "SHASUMS256.txt.sig"));
    }

    private sealed class RecordingSignatureVerifier : ISignatureVerificationService
    {
        public Exception? Failure { get; init; }

        public int CallCount { get; private set; }

        public int AllowedFingerprintsCount { get; private set; }

        public Task VerifyDetachedSignatureAsync(
            string contentPath,
            string signaturePath,
            string armoredPublicKey,
            IReadOnlySet<string> allowedFingerprints,
            CancellationToken cancellationToken = default)
        {
            Assert.IsTrue(File.Exists(contentPath));
            Assert.IsTrue(File.Exists(signaturePath));
            StringAssert.Contains(armoredPublicKey, "BEGIN PGP PUBLIC KEY BLOCK");
            CallCount++;
            AllowedFingerprintsCount = allowedFingerprints.Count;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class RecordingNodeDownloadService : IDownloadService
    {
        public const string ExpectedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly RecordingSignatureVerifier _signature;

        public RecordingNodeDownloadService(RecordingSignatureVerifier signature)
        {
            _signature = signature;
        }

        public int ArchiveDownloadCount { get; private set; }

        public string? ArchiveExpectedHash { get; private set; }

        public async Task<DownloadResult> DownloadAsync(
            Uri source,
            string destinationPath,
            string? expectedSha256 = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (destinationPath.EndsWith("SHASUMS256.txt", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(
                    destinationPath,
                    $"{ExpectedHash}  node-v1.2.3-win-x64.zip\n",
                    cancellationToken);
            }
            else if (destinationPath.EndsWith(".sig", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(destinationPath, "signature", cancellationToken);
            }
            else
            {
                if (_signature.CallCount == 0)
                {
                    throw new InvalidOperationException("Archive download started before manifest verification.");
                }

                ArchiveDownloadCount++;
                ArchiveExpectedHash = expectedSha256;
                await WriteArchiveAsync(destinationPath, cancellationToken);
            }

            var bytes = await File.ReadAllBytesAsync(destinationPath, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new DownloadResult(destinationPath, hash, bytes.Length);
        }

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken = default)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static async Task WriteArchiveAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var entry = archive.CreateEntry("node-v1.2.3-win-x64/node.exe");
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync("node"u8.ToArray(), cancellationToken);
        }
    }

    private sealed class PublicKeyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("-----BEGIN PGP PUBLIC KEY BLOCK-----\nfixture\n-----END PGP PUBLIC KEY BLOCK-----"),
                RequestMessage = request,
            });
    }
}
