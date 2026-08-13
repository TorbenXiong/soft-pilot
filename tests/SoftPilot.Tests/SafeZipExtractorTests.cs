using System.IO.Compression;
using SoftPilot.Application;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Tests;

[TestClass]
public sealed class SafeZipExtractorTests
{
    [TestMethod]
    public void Extract_StripsSingleRootDirectory()
    {
        using var sandbox = new TemporaryDirectory();
        var archive = Path.Combine(sandbox.Path, "runtime.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("node-v1/node.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("test");
        }

        var output = Path.Combine(sandbox.Path, "output");
        SafeZipExtractor.Extract(archive, output, stripSingleRootDirectory: true);
        Assert.IsTrue(File.Exists(Path.Combine(output, "node.exe")));
    }

    [TestMethod]
    public void Extract_RejectsPathTraversal()
    {
        using var sandbox = new TemporaryDirectory();
        var archive = Path.Combine(sandbox.Path, "malicious.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("bad");
        }

        Assert.Throws<IntegrityException>(() =>
            SafeZipExtractor.Extract(archive, Path.Combine(sandbox.Path, "output"), stripSingleRootDirectory: false));
    }
}
