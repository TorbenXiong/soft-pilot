using SoftPilot.Application;
using SoftPilot.Domain;
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
        Assert.AreEqual("zh-CN", preferences.Language);
        CollectionAssert.AreEqual(
            new[] { RuntimeKind.Node, RuntimeKind.Java, RuntimeKind.Python },
            preferences.GetModuleOrder().ToArray());
    }

    [TestMethod]
    public async Task SaveAsync_PersistsModuleOrder()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var store = new JsonRuntimeModulePreferencesStore(layout);
        var expectedOrder = new[] { RuntimeKind.Python, RuntimeKind.Node, RuntimeKind.Java };
        var expected = new RuntimeModulePreferences(true, true, false, "zh-CN", expectedOrder);

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        CollectionAssert.AreEqual(expectedOrder, actual.GetModuleOrder().ToArray());
    }

    [TestMethod]
    public void GetModuleOrder_RepairsMissingAndDuplicateEntries()
    {
        var preferences = new RuntimeModulePreferences(
            true,
            true,
            true,
            ModuleOrder: new[] { RuntimeKind.Python, RuntimeKind.Python });

        CollectionAssert.AreEqual(
            new[] { RuntimeKind.Python, RuntimeKind.Node, RuntimeKind.Java },
            preferences.GetModuleOrder().ToArray());
    }

    [TestMethod]
    public async Task SaveAsync_PersistsSelectedModulesAndLeavesNoTemporaryFile()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var store = new JsonRuntimeModulePreferencesStore(layout);
        var expected = new RuntimeModulePreferences(true, false, true, "en-US");

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
