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
        Assert.IsTrue(preferences.RedisEnabled);
        Assert.IsTrue(preferences.MySqlEnabled);
        Assert.IsTrue(preferences.GitEnabled);
        Assert.AreEqual("en-US", preferences.Language);
        CollectionAssert.AreEqual(
            new[] { ModuleKind.Node, ModuleKind.Java, ModuleKind.Python, ModuleKind.Redis, ModuleKind.MySql, ModuleKind.Git },
            preferences.GetModuleOrder().ToArray());
    }

    [TestMethod]
    public async Task SaveAsync_PersistsModuleOrder()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var store = new JsonRuntimeModulePreferencesStore(layout);
        var expectedOrder = new[] { ModuleKind.Python, ModuleKind.Redis, ModuleKind.MySql, ModuleKind.Git, ModuleKind.Node, ModuleKind.Java };
        var expected = new RuntimeModulePreferences(true, true, false, "zh-CN", expectedOrder);

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        CollectionAssert.AreEqual(expectedOrder, actual.GetModuleOrder().ToArray());
    }

    [TestMethod]
    public async Task LoadAsync_LegacyPreferencesEnableNewServiceModulesAndAppendThemToOrder()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        Directory.CreateDirectory(layout.DataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(layout.DataDirectory, "ui-preferences.json"),
            """
            {
              "nodeEnabled": true,
              "javaEnabled": false,
              "pythonEnabled": true,
              "language": "zh-CN",
              "moduleOrder": [2, 0, 1]
            }
            """);
        var store = new JsonRuntimeModulePreferencesStore(layout);

        var preferences = await store.LoadAsync();

        Assert.IsTrue(preferences.RedisEnabled);
        Assert.IsTrue(preferences.MySqlEnabled);
        Assert.IsTrue(preferences.GitEnabled);
        CollectionAssert.AreEqual(
            new[] { ModuleKind.Python, ModuleKind.Node, ModuleKind.Java, ModuleKind.Redis, ModuleKind.MySql, ModuleKind.Git },
            preferences.GetModuleOrder().ToArray());
    }

    [TestMethod]
    public void GetModuleOrder_RepairsMissingAndDuplicateEntries()
    {
        var preferences = new RuntimeModulePreferences(
            true,
            true,
            true,
            ModuleOrder: new[] { ModuleKind.Python, ModuleKind.Python });

        CollectionAssert.AreEqual(
            new[] { ModuleKind.Python, ModuleKind.Node, ModuleKind.Java, ModuleKind.Redis, ModuleKind.MySql, ModuleKind.Git },
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
