using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SoftPilot.Infrastructure.Installation;

namespace SoftPilot.Tests;

[TestClass]
public sealed class PortableToolsProvisionerTests
{
    [TestMethod]
    public async Task ProvisionAsync_ExtractsToolsAndCreatesShimAliases()
    {
        using var sandbox = new TemporaryDirectory();
        var tools = Path.Combine(sandbox.Path, "SoftPilotData", "tools");
        await using var archive = CreateArchive();

        await new PortableToolsProvisioner().ProvisionAsync(archive, tools);

        Assert.AreEqual("cli", File.ReadAllText(Path.Combine(tools, "spt.exe")));
        Assert.AreEqual("cli", File.ReadAllText(Path.Combine(tools, "shims", "spt.exe")));
        Assert.AreEqual("shim", File.ReadAllText(Path.Combine(tools, "shims", "node.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(tools, "shims", "python3.exe")));
    }

    [TestMethod]
    public async Task ProvisionAsync_WhenManifestMatches_KeepsExistingDirectory()
    {
        using var sandbox = new TemporaryDirectory();
        var tools = Path.Combine(sandbox.Path, "SoftPilotData", "tools");
        await using (var first = CreateArchive())
        {
            await new PortableToolsProvisioner().ProvisionAsync(first, tools);
        }

        var marker = Path.Combine(tools, "keep.txt");
        File.WriteAllText(marker, "keep");
        await using var second = CreateArchive();
        await new PortableToolsProvisioner().ProvisionAsync(second, tools);

        Assert.IsTrue(File.Exists(marker));
    }

    private static MemoryStream CreateArchive()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["spt.exe"] = Encoding.UTF8.GetBytes("cli"),
            ["shims/SoftPilot.Shim.exe"] = Encoding.UTF8.GetBytes("shim"),
        };
        var manifest = string.Join(
            "\n",
            files.Select(pair => $"{Convert.ToHexString(SHA256.HashData(pair.Value))}  {pair.Key}")) + "\n";

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var output = entry.Open();
                output.Write(content);
            }

            var manifestEntry = archive.CreateEntry(PortableToolsProvisioner.ManifestName);
            using var manifestOutput = new StreamWriter(
                manifestEntry.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            manifestOutput.Write(manifest);
        }

        stream.Position = 0;
        return stream;
    }
}
