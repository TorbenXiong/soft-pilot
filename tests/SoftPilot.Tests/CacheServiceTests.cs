using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Tests;

[TestClass]
public sealed class CacheServiceTests
{
    [TestMethod]
    public async Task CleanExpiredAsync_RemovesOnlyEntriesOlderThanRetentionPeriod()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        layout.EnsureWorkspace();
        var nested = Path.Combine(layout.DownloadsDirectory, "python-manager");
        Directory.CreateDirectory(nested);
        var expired = Path.Combine(nested, "expired.pkg");
        var recent = Path.Combine(layout.DownloadsDirectory, "recent.zip");
        await File.WriteAllTextAsync(expired, "expired");
        await File.WriteAllTextAsync(recent, "recent");
        File.SetLastWriteTimeUtc(
            expired,
            DateTime.UtcNow - CacheService.RetentionPeriod - TimeSpan.FromDays(1));

        await new CacheService(layout).CleanExpiredAsync();

        Assert.IsFalse(File.Exists(expired));
        Assert.IsFalse(Directory.Exists(nested));
        Assert.IsTrue(File.Exists(recent));
    }
}
