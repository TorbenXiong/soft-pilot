using System.Text;
using SoftPilot.Application;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Tools;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WindowsToolboxMaintenanceServiceTests
{
    [TestMethod]
    public void ValidateEnvironmentVariableName_NormalizesValidName()
    {
        var actual = WindowsEnvironmentVariableService.ValidateName("  SOFTPILOT_TEST  ");

        Assert.AreEqual("SOFTPILOT_TEST", actual);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("INVALID=NAME")]
    public void ValidateEnvironmentVariableName_RejectsInvalidName(string name)
    {
        Assert.ThrowsExactly<SoftPilotException>(() =>
            WindowsEnvironmentVariableService.ValidateName(name));
    }

    [TestMethod]
    public void EnvironmentPathValue_RoundTripsOrderAndEmptyEntries()
    {
        const string original = @"C:\Tools;;%JAVA_HOME%\bin;";

        var entries = EnvironmentPathValue.Split(original);
        var actual = EnvironmentPathValue.Join(entries);

        CollectionAssert.AreEqual(
            new[] { @"C:\Tools", string.Empty, @"%JAVA_HOME%\bin", string.Empty },
            entries.ToArray());
        Assert.AreEqual(original, actual);
    }

    [TestMethod]
    public void EnvironmentPathValue_RejectsEmbeddedSeparator()
    {
        Assert.ThrowsExactly<SoftPilotException>(() =>
            EnvironmentPathValue.Join([@"C:\Tools;C:\Other"]));
    }

    [TestMethod]
    public void EnvironmentPathValue_ResolveMarksExistingAndMissingDirectories()
    {
        using var temporary = new TemporaryDirectory();

        var existing = EnvironmentPathValue.Resolve($"\"{temporary.Path}\"");
        var missing = EnvironmentPathValue.Resolve(Path.Combine(temporary.Path, "missing"));

        Assert.IsTrue(existing.Exists);
        Assert.AreEqual($"\"{temporary.Path}\"", existing.ExpandedValue);
        Assert.IsFalse(missing.Exists);
    }

    [TestMethod]
    public async Task HostsService_ReadsSavesAndBacksUpOriginalContent()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var hostsPath = Path.Combine(temporary.Path, "system", "drivers", "etc", "hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(hostsPath)!);
        const string original = "127.0.0.1 localhost\r\n# original\r\n";
        const string updated = "127.0.0.1 localhost\r\n127.0.0.1 local.test\r\n";
        await File.WriteAllTextAsync(hostsPath, original, new UTF8Encoding(false));
        var service = new WindowsHostsFileService(layout, hostsPath);

        Assert.AreEqual(original, await service.ReadAsync());
        await service.SaveAsync(updated);

        Assert.AreEqual(updated, await File.ReadAllTextAsync(hostsPath));
        var backups = Directory.GetFiles(
            Path.Combine(layout.DataDirectory, "toolbox", "hosts-backups"),
            "hosts-*.bak");
        Assert.HasCount(1, backups);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(original),
            await File.ReadAllBytesAsync(backups[0]));
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(hostsPath)!, "*.tmp"));
    }

    [TestMethod]
    public async Task HostsService_PreservesUtf8BomWhenSaving()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var hostsPath = Path.Combine(temporary.Path, "hosts");
        await File.WriteAllTextAsync(hostsPath, "# 初始\r\n", new UTF8Encoding(true));
        var service = new WindowsHostsFileService(layout, hostsPath);

        await service.SaveAsync("# 更新\r\n");

        var bytes = await File.ReadAllBytesAsync(hostsPath);
        Assert.IsTrue(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    }

    [TestMethod]
    public async Task HostsService_RejectsNullCharactersWithoutChangingFile()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var hostsPath = Path.Combine(temporary.Path, "hosts");
        await File.WriteAllTextAsync(hostsPath, "127.0.0.1 localhost\r\n");
        var service = new WindowsHostsFileService(layout, hostsPath);

        await Assert.ThrowsAsync<SoftPilotException>(() => service.SaveAsync("127.0.0.1\0localhost"));

        Assert.AreEqual("127.0.0.1 localhost\r\n", await File.ReadAllTextAsync(hostsPath));
    }

    [TestMethod]
    public async Task HostsService_RejectsUnsupportedEncodingWithoutRewritingFile()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var hostsPath = Path.Combine(temporary.Path, "hosts");
        byte[] original = [0x31, 0x32, 0x37, 0x20, 0xFF];
        await File.WriteAllBytesAsync(hostsPath, original);
        var service = new WindowsHostsFileService(layout, hostsPath);

        await Assert.ThrowsAsync<SoftPilotException>(() => service.ReadAsync());
        await Assert.ThrowsAsync<SoftPilotException>(() => service.SaveAsync("127.0.0.1 localhost\r\n"));

        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(hostsPath));
    }
}
