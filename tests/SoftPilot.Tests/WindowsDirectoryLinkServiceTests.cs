using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WindowsDirectoryLinkServiceTests
{
    [TestMethod]
    public async Task ReplaceAsync_CreatesAndReplacesDirectoryLink()
    {
        using var sandbox = new TemporaryDirectory();
        var first = Path.Combine(sandbox.Path, "first target");
        var second = Path.Combine(sandbox.Path, "second+target");
        var link = Path.Combine(sandbox.Path, "current", "java");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(first, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(second, "second.txt"), "second");
        var service = new WindowsDirectoryLinkService(new ProcessRunner());

        await service.ReplaceAsync(link, first, CancellationToken.None);
        Assert.IsTrue(File.Exists(Path.Combine(link, "first.txt")));

        await service.ReplaceAsync(link, second, CancellationToken.None);
        Assert.IsFalse(File.Exists(Path.Combine(link, "first.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(link, "second.txt")));

        service.Delete(link);
        Assert.IsFalse(Directory.Exists(link));
        Assert.IsTrue(Directory.Exists(first));
        Assert.IsTrue(Directory.Exists(second));
    }
}
