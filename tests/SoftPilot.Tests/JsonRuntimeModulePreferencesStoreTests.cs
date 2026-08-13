using SoftPilot.Application;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.State;

namespace SoftPilot.Tests;

[TestClass]
public sealed class JsonRuntimeModulePreferencesStoreTests
{
    [TestMethod]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsAllModulesEnabled()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var store = new JsonRuntimeModulePreferencesStore(layout);

        var preferences = await store.LoadAsync();

        Assert.IsTrue(preferences.NodeEnabled);
        Assert.IsTrue(preferences.JavaEnabled);
        Assert.IsTrue(preferences.PythonEnabled);
    }

    [TestMethod]
    public async Task SaveAsync_PersistsSelectedModulesAndLeavesNoTemporaryFile()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var store = new JsonRuntimeModulePreferencesStore(layout);
        var expected = new RuntimeModulePreferences(true, false, true);

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        Assert.AreEqual(expected, actual);
        Assert.AreEqual(
            0,
            Directory.GetFiles(layout.DataDirectory, "*.tmp", SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public async Task LoadAsync_WhenJsonIsInvalid_ThrowsSafeConfigurationError()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        Directory.CreateDirectory(layout.DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(layout.DataDirectory, "ui-preferences.json"), "not-json");
        var store = new JsonRuntimeModulePreferencesStore(layout);

        var exception = await Assert.ThrowsAsync<SoftPilotException>(() => store.LoadAsync());

        StringAssert.Contains(exception.Message, "已损坏");
    }
}
